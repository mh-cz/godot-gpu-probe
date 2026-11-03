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
    // 32x2 = 256 points per batch, a sweet spot for most use cases
    private const int PROBE_WIDTH = 32;
    private const int PROBE_HEIGHT = 2;

    private const int MAX_TEXTURES = 8;  // Texture slot count. Must match the shader's MAX_TEXTURES define
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

    private List<ProbeRequest> queue = [];
    private Dictionary<int, ProbeRequest> dict = [];
    private Stack<ProbeRequest> pool = new();
    private List<ProbeRequest> batch = [];

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

    public delegate void ProbeCallback(ReadOnlySpan<byte> values);


    private class ProbeRequest
    {
        public int id_hash;
        public int[] data = new int[MAX_BATCH_COUNT * 4];
        public int count;
        public bool urgent;
        public ProbeCallback callback;
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
        readback_done = new Callable(this, MethodName.OnReadbackDone);
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
        => Inst.SetTex(slot, texture, out_of_bounds_color);


    private void SetTex(int slot, Texture2D texture, Color out_of_bounds)
    {
        if (slot < 0 || slot >= MAX_TEXTURES)
            throw new ArgumentOutOfRangeException(nameof(slot), $"[GPUProbe] slot must be 0..{MAX_TEXTURES - 1}");

        textures[slot] = texture ?? empty_slot_tex;
        oob_colors[slot] = texture != null ? out_of_bounds : default;
        mat.SetShaderParameter(sn_textures, textures);
        mat.SetShaderParameter(sn_oob_colors, oob_colors);
    }


    /// <summary>
    /// Requests the GPU to read back the given points from the given texture slots
    /// The callback is called once the GPU has finished rendering and reading back the data
    /// </summary>
    public static void ProbePosition(ulong caller_id, int call_id, ReadOnlySpan<(int x, int y, Channel chn, int slot)> points, ProbeCallback callback, bool urgent = false)
        => Inst.ProbePos(caller_id, call_id, points, callback, urgent);
    

    private void ProbePos(ulong caller_id, int call_id, ReadOnlySpan<(int x, int y, Channel chn, int slot)> points, ProbeCallback callback, bool urgent)
    {
        int[] data = GetRequestBuffer(caller_id, call_id, points.Length, callback, urgent);

        for (int i = 0; i < points.Length; i++)
        {
            var (x, y, chn, slot) = points[i];
            int o = i * 4;
            data[o] = x;
            data[o + 1] = y;
            data[o + 2] = (int)chn;
            data[o + 3] = slot;
        }
    }


    /// <summary>
    /// Requests the GPU to read back the given points from the given texture slots
    /// The callback is called once the GPU has finished rendering and reading back the data
    /// </summary>
    public static void ProbePosition(ulong caller_id, int call_id, ReadOnlySpan<(Vector2I pos, Channel chn, int slot)> points, ProbeCallback callback, bool urgent = false)
        => Inst.ProbePos(caller_id, call_id, points, callback, urgent);


    private void ProbePos(ulong caller_id, int call_id, ReadOnlySpan<(Vector2I pos, Channel chn, int slot)> points, ProbeCallback callback, bool urgent)
    {
        int[] data = GetRequestBuffer(caller_id, call_id, points.Length, callback, urgent);

        for (int i = 0; i < points.Length; i++)
        {
            var (pos, chn, slot) = points[i];
            int o = i * 4;
            data[o] = pos.X;
            data[o + 1] = pos.Y;
            data[o + 2] = (int)chn;
            data[o + 3] = slot;
        }
    }


    // Finds the queued request for (caller_id, call_id) or takes one from the pool
    // Sets its count and callback
    // And returns its buffer for the caller to fill with (x, y, chn, slot) params
    private int[] GetRequestBuffer(ulong caller_id, int call_id, int count, ProbeCallback callback, bool urgent)
    {
        if (count > MAX_BATCH_COUNT)
            throw new ArgumentException($"[GPUProbe] {count} points requested, max per request is {MAX_BATCH_COUNT}", nameof(count));

        int key = HashCode.Combine(caller_id, call_id);

        if (!dict.TryGetValue(key, out var req))
        {
            req = pool.Count > 0 ? pool.Pop() : new ProbeRequest();
            req.id_hash = key;
            req.urgent = false;
            queue.Add(req);
            dict[key] = req;
        }

        // Urgent requests skip the throttle
        if (urgent && !req.urgent)
        {
            req.urgent = true;
            queue.Remove(req);
            queue.Insert(0, req);
        }
        urgent_queued |= urgent;

        req.count = count;
        req.callback = callback;
        return req.data;
    }


    public override void _PhysicsProcess(double delta)
    {
        if (state == State.IDLE && queue.Count > 0 && (urgent_queued || Time.GetTicksMsec() - last_read_ms >= THROTTLE_MS))
            SubmitBatch();
    }


    private void SubmitBatch()
    {
        Span<float> q = MemoryMarshal.Cast<byte, float>(query_bytes);
        int used = 0, n = 0;

        while (n < queue.Count && used + queue[n].count <= MAX_BATCH_COUNT)
        {
            var req = queue[n];
            int[] data = req.data;
            int base_i = used * 4;
            for (int i = 0; i < req.count * 4; i++)
                q[base_i + i] = data[i];
            used += req.count;

            dict.Remove(req.id_hash);
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
        Deliver(img.GetData());
    }


    private void OnReadbackDone(byte[] data)
    {
        if (state != State.READBACK) 
            return;

        readback_data = data;
        CallDeferred(MethodName.DeliverReadback);
    }


    private void DeliverReadback()
    {
        byte[] data = readback_data;
        readback_data = null;
        Deliver(data);
    }


    private void Deliver(byte[] raw)
    {
        last_read_ms = Time.GetTicksMsec();
        int head = 0;

        foreach (var req in batch)
        {
            try
            {
                req.callback?.Invoke(new ReadOnlySpan<byte>(raw, head, req.count));
            }
            catch (Exception e)
            {
                GD.PushError($"[GPUProbe] callback threw: {e}");
            }

            head += req.count;
            req.callback = null;
            req.urgent = false;
            pool.Push(req);
        }

        batch.Clear();
        state = State.IDLE;
    }
}
