using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;


public partial class GPUProbe : CanvasLayer
{
    public enum Channel { R = 0, G = 1, B = 2, A = 3 }
    private enum State { IDLE, RENDERING, READBACK }

    public static ulong THROTTLE_MS = 100;              // Minimum time between readbacks in milliseconds to avoid blocking the GPU
    public static int COMPATIBILITY_WAIT_FRAMES = 2;    // Frames to wait before reading back on the Compatibility renderer (OpenGL) to avoid blocking the GPU

    // The larger the probe, the more points can be read back in a single batch but the longer the GPU will stall
    // 32x2 = 256 points (bytes) per batch, a sweet spot for most use cases
    private const int PROBE_WIDTH = 32;
    private const int PROBE_HEIGHT = 2;

    private const int MAX_TEXTURES = 8;  // Must match the shader's MAX_TEXTURES define
    private const int MAX_BATCH_COUNT = PROBE_WIDTH * PROBE_HEIGHT * 4;  // W * H * RGBA

    public static string AUTOLOAD_NAME { get; private set;} = "GPUProbe";
    public static string SCENE_PATH { get; private set;} = "res://addons/gpu_probe/GPUProbe.tscn";

    private static readonly StringName sn_textures = new("textures");
    private static readonly StringName sn_oob_colors = new("oob_colors");
    private static readonly StringName sn_query_tex = new("query_tex");
    private static readonly StringName sn_probe_size = new("probe_size");

    private static GPUProbe inst;
    private static GPUProbe Inst => inst ?? throw new InvalidOperationException(
        "[GPUProbe] not running. Enable the GPU Probe addon so the autoload can be registered");

    private Godot.Collections.Array<Texture2D> textures = [];
    private Texture2D empty_slot_tex;
    private Color[] oob_colors = new Color[MAX_TEXTURES];
    private Texture2D tex;
    private SubViewport probe_sbv;
    private ColorRect rect;
    private ShaderMaterial mat;

    private List<Request> queue = [];
    private List<Request> batch = [];

    private byte[] query_bytes;
    private Image query_img;
    private ImageTexture query_tex;

    private State state = State.IDLE;
    private ulong last_read_ms;
    private int render_frame;
    private bool urgent_queued;

    // Forward+ and Mobile renderers (Vulkan/D3D12/Metal) use asynchronous readback
    // Compatibility renderer (OpenGL) uses synchronous immediate readback (rd == null)
    private RenderingDevice rd;

    private Callable readback_done;
    private byte[] readback_data;


    // Both (x, y, chn, slot) and (pos, chn, slot) tuples convert implicitly, so they can be mixed in one Submit list
    public readonly struct Point(int x, int y, Channel chn, int slot)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly Channel Chn = chn;
        public readonly int Slot = slot;

        public static implicit operator Point((int x, int y, Channel chn, int slot) p)
            => new(p.x, p.y, p.chn, p.slot);

        public static implicit operator Point((Vector2I pos, Channel chn, int slot) p)
            => new(p.pos.X, p.pos.Y, p.chn, p.slot);
    }


    public sealed class Request
    {
        public Action<Request> Callback;
        public int Count { get; private set; }

        internal int[] pending_points = [];
        internal int[] result_points = [];
        internal byte[] results = [];
        internal int result_count;
        internal int sent_count;
        internal bool queued;
        internal bool urgent;
        internal bool cancelled;


        public Request(Action<Request> callback = null)
            => Callback = callback;

        public void Cancel()
            => Inst.CancelRequest(this);

        public ReadOnlySpan<byte> Result
            => new(results, 0, result_count);


        public void Submit(ReadOnlySpan<Point> pts, bool urgent = false)
        {
            int[] p = ReservePointBuffer(pts.Length);

            for (int i = 0; i < pts.Length; i++)
            {
                var pt = pts[i];
                int o = i * 4;
                p[o    ] = pt.X;
                p[o + 1] = pt.Y;
                p[o + 2] = (int)pt.Chn;
                p[o + 3] = pt.Slot;
            }

            Inst.EnqueueRequest(this, urgent);
        }


        public (Vector2I pos, Channel chn, int slot) GetResultPoint(int i)
        {
            if ((uint)i >= (uint)result_count)
                throw new ArgumentOutOfRangeException(nameof(i));

            int o = i * 4;
            return (new Vector2I(result_points[o], result_points[o + 1]), (Channel)result_points[o + 2], result_points[o + 3]);
        }


        private int[] ReservePointBuffer(int count)
        {
            if (count > MAX_BATCH_COUNT)
                throw new ArgumentException($"[GPUProbe] {count} points requested, max per request is {MAX_BATCH_COUNT}", nameof(count));

            if (pending_points.Length < count * 4)
                pending_points = new int[count * 4];

            Count = count;
            return pending_points;
        }


        internal void StoreReadbackResults(ReadOnlySpan<byte> values, ReadOnlySpan<float> query)
        {
            if (results.Length < values.Length)
            {
                results = new byte[values.Length];
                result_points = new int[values.Length * 4];
            }

            values.CopyTo(results);
            for (int i = 0; i < query.Length; i++)
                result_points[i] = (int)query[i];
            result_count = values.Length;
        }
    }


    public override void _EnterTree()
    {
        if (inst != null && inst != this)
        {
            GD.PushWarning("[GPUProbe] freeing old instance");
            QueueFree();
            return;
        }

        inst = this;
        ProcessMode = ProcessModeEnum.Always;
    }


    public override void _ExitTree()
    {
        if (inst == this)
            inst = null;
    }


    public override void _Ready()
    {
        if (inst != this) return;

        probe_sbv = GetNode<SubViewport>("ProbeViewport");
        rect = GetNode<ColorRect>("ProbeViewport/QueryRect");
        mat = (ShaderMaterial)rect.Material;

        if (PROBE_WIDTH < 2 || PROBE_HEIGHT < 2)
            throw new ArgumentException($"[GPUProbe] PROBE_WIDTH and PROBE_HEIGHT must be >= 2, got {PROBE_WIDTH}x{PROBE_HEIGHT}");

        var probe_size = new Vector2I(PROBE_WIDTH, PROBE_HEIGHT);

        probe_sbv.SetDeferred(SubViewport.PropertyName.Size, probe_size);
        rect.Size = probe_size;
        probe_sbv.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        tex = probe_sbv.GetTexture();

        rd = RenderingServer.GetRenderingDevice();
        readback_done = new Callable(this, MethodName.OnAsyncReadbackDone);
        RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePostDraw, new Callable(this, MethodName.OnFramePostDraw));

        query_bytes = new byte[MAX_BATCH_COUNT * 4 * sizeof(float)];
        query_img = Image.CreateFromData(MAX_BATCH_COUNT, 1, false, Image.Format.Rgbaf, query_bytes);
        query_tex = ImageTexture.CreateFromImage(query_img);

        mat.SetShaderParameter(sn_query_tex, query_tex);
        mat.SetShaderParameter(sn_probe_size, probe_size);

        empty_slot_tex = ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));
        for (int i = 0; i < MAX_TEXTURES; i++)
            textures.Add(empty_slot_tex);

        mat.SetShaderParameter(sn_textures, textures);
        mat.SetShaderParameter(sn_oob_colors, oob_colors);
    }


    /// <summary>
    /// Sets the texture to read from in the given texture slot
    /// </summary>
    public static void SetTexture(int slot, Texture2D texture, Color out_of_bounds_color = default)
        => Inst.SetTextureSlot(slot, texture, out_of_bounds_color);


    private void SetTextureSlot(int slot, Texture2D texture, Color out_of_bounds)
    {
        if (slot < 0 || slot >= MAX_TEXTURES)
            throw new ArgumentOutOfRangeException(nameof(slot), $"[GPUProbe] slot must be 0..{MAX_TEXTURES - 1}");

        textures[slot] = texture ?? empty_slot_tex;
        oob_colors[slot] = texture != null ? out_of_bounds : default;
        mat.SetShaderParameter(sn_textures, textures);
        mat.SetShaderParameter(sn_oob_colors, oob_colors);
    }


    private void EnqueueRequest(Request req, bool urgent)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!req.queued)
        {
            req.queued = true;
            req.urgent = false;
            queue.Add(req);
        }

        if (urgent && !req.urgent)
        {
            req.urgent = true;
            queue.Remove(req);
            queue.Insert(0, req);
        }
        urgent_queued |= urgent;
    }


    private void CancelRequest(Request req)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (req.queued)
        {
            queue.Remove(req);
            req.queued = false;
            req.urgent = false;
            urgent_queued = queue.Count > 0 && queue[0].urgent;
        }

        req.cancelled = true;
    }


    public override void _PhysicsProcess(double delta)
    {
        if (state == State.IDLE && queue.Count > 0 && (urgent_queued || Time.GetTicksMsec() - last_read_ms >= THROTTLE_MS))
            RenderNextBatch();
    }


    private void RenderNextBatch()
    {
        Span<float> q = MemoryMarshal.Cast<byte, float>(query_bytes);
        int used = 0, n = 0;

        while(n < queue.Count && used + queue[n].Count <= MAX_BATCH_COUNT)
        {
            var req = queue[n];
            int[] data = req.pending_points;
            int base_i = used * 4;
            for (int i = 0; i < req.Count * 4; i++)
                q[base_i + i] = data[i];
            used += req.Count;

            req.sent_count = req.Count;
            req.queued = false;
            req.urgent = false;
            req.cancelled = false;
            batch.Add(req);
            n++;
        }

        if (n == 0) return;
        queue.RemoveRange(0, n);

        urgent_queued = queue.Count > 0 && queue[0].urgent;

        for (int i = used * 4; i < q.Length; i += 4)
        {
            q[i] = -1;
            q[i + 1] = -1;
            q[i + 2] = 0;
            q[i + 3] = 0;
        }

        query_img.SetData(MAX_BATCH_COUNT, 1, false, Image.Format.Rgbaf, query_bytes);
        query_tex.Update(query_img);
        probe_sbv.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        render_frame = Engine.GetFramesDrawn();
        state = State.RENDERING;
    }


    private void OnFramePostDraw()
    {
        if (state != State.RENDERING) return;

        if (rd != null)
        {
            Rid rd_tex = RenderingServer.TextureGetRdTexture(tex.GetRid());
            if (rd_tex.IsValid)
            {
                // Forward+ and Mobile (Vulkan/D3D12/Metal)
                Error err = rd.TextureGetDataAsync(rd_tex, 0, readback_done);
                if (err == Error.Ok)
                {
                    state = State.READBACK;
                    return;
                }

                GD.PushWarning($"[GPUProbe] TextureGetDataAsync failed: ({err}). using synchronous readback");
            }
        }

        // Compatibility (OpenGL)
        if (Engine.GetFramesDrawn() < render_frame + COMPATIBILITY_WAIT_FRAMES)
            return;

        using Image img = tex.GetImage();
        DeliverBatchResults(img.GetData());
    }


    private void OnAsyncReadbackDone(byte[] data)
    {
        if (state != State.READBACK)
            return;

        readback_data = data;
        CallDeferred(MethodName.DeliverAsyncReadback);
    }


    private void DeliverAsyncReadback()
    {
        byte[] data = readback_data;
        readback_data = null;
        DeliverBatchResults(data);
    }


    private void DeliverBatchResults(byte[] raw)
    {
        last_read_ms = Time.GetTicksMsec();
        ReadOnlySpan<float> q = MemoryMarshal.Cast<byte, float>(query_bytes);
        int head = 0;

        foreach (var req in batch)
        {
            int count = req.sent_count;
            bool freed = req.Callback?.Target is GodotObject target && !IsInstanceValid(target);
            
            if (!req.cancelled && !freed)
            {
                req.StoreReadbackResults(new ReadOnlySpan<byte>(raw, head, count), q.Slice(head * 4, count * 4));

                try
                {
                    req.Callback?.Invoke(req);
                }
                catch (Exception e)
                {
                    GD.PushError($"[GPUProbe] callback threw: {e}");
                }
            }

            head += count;
        }

        batch.Clear();
        state = State.IDLE;
    }
}
