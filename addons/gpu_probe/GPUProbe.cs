using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;


public partial class GPUProbe : CanvasLayer
{
    public enum Channel { R = 0, G = 1, B = 2, A = 3 }
    private enum State { IDLE, RENDERING, READBACK }

    // minimum time between readbacks in milliseconds to avoid blocking the GPU
    public static ulong THROTTLE_MS = 100;

    // frames to wait before reading back on the Compatibility renderer (OpenGL) to avoid blocking the GPU
    public static int COMPATIBILITY_WAIT_FRAMES = 2;

    // skip the throttle if a batch ran out of texture slots so the leftovers get processed rigth after without waiting
    public static bool TEXTURE_OVERFLOW_SKIP_THROTTLE = false;

    // the larger the probe, the more points can be read back in a single batch but the longer the GPU may stall
    // 32x2 = 256 points (bytes) per batch, a sweet spot for most use cases
    private const int PROBE_WIDTH = 32;
    private const int PROBE_HEIGHT = 2;

    // do not change
    private const int MAX_TEXTURES_PER_BATCH = 16;
    private const int MAX_BATCH_COUNT = PROBE_WIDTH * PROBE_HEIGHT * 4;

    public static string AUTOLOAD_NAME { get; private set;} = "GPUProbe";
    public static string SCENE_PATH { get; private set;} = "res://addons/gpu_probe/GPUProbe.tscn";

    private static readonly StringName sn_textures = new("textures");
    private static readonly StringName sn_oob_colors = new("oob_colors");
    private static readonly StringName sn_query_tex = new("query_tex");
    private static readonly StringName sn_probe_size = new("probe_size");
    private static readonly StringName sn_used_slots = new("used_slots");

    private static GPUProbe inst;
    private static GPUProbe Inst => inst ?? throw new InvalidOperationException(
        "[GPUProbe] not running. Enable the GPU Probe addon so the autoload can be registered");

    private readonly Texture2D[] batch_textures = new Texture2D[MAX_TEXTURES_PER_BATCH];
    private readonly Texture2D[] applied_textures = new Texture2D[MAX_TEXTURES_PER_BATCH];
    private readonly Color[] batch_oob = new Color[MAX_TEXTURES_PER_BATCH];
    private readonly Color[] applied_oob = new Color[MAX_TEXTURES_PER_BATCH];

    private int applied_used_slots = -1;
    private Texture2D empty_slot_tex;
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
    private bool tex_slots_overflowed;

    // Forward+ and Mobile renderers (Vulkan/D3D12/Metal) use asynchronous readback
    // Compatibility renderer (OpenGL) uses synchronous immediate readback (rd == null)
    private RenderingDevice rd;

    private Callable readback_done;
    private byte[] readback_data;


    // Both (x, y, chn, texture) and (pos, chn, texture) tuples convert implicitly so they can be mixed in one Submit list
    public readonly struct Point(int x, int y, Channel chn, Texture2D texture)
    {
        public readonly int x = x;
        public readonly int y = y;
        public readonly Channel chn = chn;
        public readonly Texture2D texture = texture;

        public static implicit operator Point((int x, int y, Channel chn, Texture2D texture) p)
            => new(p.x, p.y, p.chn, p.texture);

        public static implicit operator Point((Vector2I pos, Channel chn, Texture2D texture) p)
            => new(p.pos.X, p.pos.Y, p.chn, p.texture);
    }


    public sealed class Request
    {
        public Action<Request> Callback;
        public int Count { get; private set; }  // Number of points set by the last Submit

        internal int[] pending_points = [];  // (x, y, chn, texture index into textures) per point
        internal Texture2D[] textures = new Texture2D[MAX_TEXTURES_PER_BATCH];  // distinct textures of the pending points
        internal int texture_count;
        internal readonly int[] slot_map = new int[MAX_TEXTURES_PER_BATCH];     // texture index -> batch slot, filled when the batch is packed
        internal Color oob;

        private Texture2D[] scratch_textures = new Texture2D[MAX_TEXTURES_PER_BATCH];
        internal int[] result_points = [];   // (x, y, chn, batch slot) per point of the last delivery
        private readonly Texture2D[] result_slot_textures = new Texture2D[MAX_TEXTURES_PER_BATCH];  // batch slot -> texture of the last delivery
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


        /// <summary>
        /// Queues the points to be read back from the GPU. One request can read from up to 16 different textures.
        /// Points outside their texture return out_of_bounds_color (Color(0,0,0,0) by default)
        /// </summary>
        public void Submit(ReadOnlySpan<Point> pts, bool urgent = false, Color out_of_bounds_color = default)
        {
            if (pts.Length > MAX_BATCH_COUNT)
                throw new ArgumentException($"[GPUProbe] {pts.Length} points requested, max per request is {MAX_BATCH_COUNT}", nameof(pts));

            // validate and collect the distinct textures before touching the request since it may already be queued
            int tex_count = 0;
            foreach (var pt in pts)
            {
                if (pt.texture == null)
                    throw new ArgumentException("[GPUProbe] every point needs a texture", nameof(pts));
                if (TextureIndex(scratch_textures, ref tex_count, pt.texture) < 0)
                    throw new ArgumentException($"[GPUProbe] a request can read from at most {MAX_TEXTURES_PER_BATCH} different textures", nameof(pts));
            }

            (textures, scratch_textures) = (scratch_textures, textures);
            Array.Clear(scratch_textures);  // don't keep the old textures alive
            texture_count = tex_count;

            if (pending_points.Length < pts.Length * 4)
                pending_points = new int[pts.Length * 4];

            for (int i = 0; i < pts.Length; i++)
            {
                var pt = pts[i];
                int o = i * 4;
                pending_points[o    ] = pt.x;
                pending_points[o + 1] = pt.y;
                pending_points[o + 2] = (int)pt.chn;
                pending_points[o + 3] = TextureIndex(textures, ref tex_count, pt.texture);
            }

            Count = pts.Length;
            oob = out_of_bounds_color;
            Inst.EnqueueRequest(this, urgent);
        }


        public (Vector2I pos, Channel chn, Texture2D texture) GetResultPoint(int i)
        {
            if ((uint)i >= (uint)result_count)
                throw new ArgumentOutOfRangeException(nameof(i));

            int o = i * 4;
            return (new Vector2I(result_points[o], result_points[o + 1]), (Channel)result_points[o + 2], result_slot_textures[result_points[o + 3]]);
        }


        // Returns the index of tex in texs, adding it if it's new, or -1 if there are too many textures
        private static int TextureIndex(Texture2D[] texs, ref int count, Texture2D tex)
        {
            Rid rid = tex.GetRid();
            for (int t = 0; t < count; t++)
                if (texs[t].GetRid() == rid)
                    return t;

            if (count == MAX_TEXTURES_PER_BATCH)
                return -1;

            texs[count] = tex;
            return count++;
        }


        internal void StoreReadbackResults(ReadOnlySpan<byte> values, ReadOnlySpan<float> query, Texture2D[] slot_textures)
        {
            if (results.Length < values.Length)
            {
                results = new byte[values.Length];
                result_points = new int[values.Length * 4];
            }

            values.CopyTo(results);
            for (int i = 0; i < query.Length; i++)
                result_points[i] = (int)query[i];
            slot_textures.CopyTo(result_slot_textures, 0);
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
        ApplySlots(0, force: true);
    }


    // Maps every texture of the request to a batch slot, reusing slots that already hold the same
    // texture and out-of-bounds color. Returns the new used slot count, or -1 if the slots ran out
    private int AssignSlots(Request req, int used_slots)
    {
        for (int t = 0; t < req.texture_count; t++)
        {
            Texture2D texture = req.textures[t];
            Rid rid = texture.GetRid();
            int slot = -1;
            for (int s = 0; s < used_slots; s++)
            {
                if (batch_oob[s] == req.oob && batch_textures[s].GetRid() == rid)
                {
                    slot = s;
                    break;
                }
            }

            if (slot < 0)
            {
                if (used_slots == MAX_TEXTURES_PER_BATCH)
                    return -1;  // slots written past the old count are ignored and cleared by ApplySlots
                slot = used_slots++;
                batch_textures[slot] = texture;
                batch_oob[slot] = req.oob;
            }

            req.slot_map[t] = slot;
        }
        return used_slots;
    }


    // Clears the unused slots and pushes the slot uniforms to the material if they changed
    private void ApplySlots(int used_slots, bool force = false)
    {
        for (int s = used_slots; s < MAX_TEXTURES_PER_BATCH; s++)
        {
            batch_textures[s] = empty_slot_tex;  // don't keep textures from older batches alive
            batch_oob[s] = default;
        }

        bool changed = force || !batch_oob.AsSpan().SequenceEqual(applied_oob);
        for (int s = 0; s < MAX_TEXTURES_PER_BATCH && !changed; s++)
            changed = !ReferenceEquals(batch_textures[s], applied_textures[s]);

        if (changed)
        {
            batch_textures.CopyTo(applied_textures, 0);
            batch_oob.CopyTo(applied_oob, 0);
            mat.SetShaderParameter(sn_textures, new Godot.Collections.Array<Texture2D>(applied_textures));
            mat.SetShaderParameter(sn_oob_colors, applied_oob);
        }

        if (used_slots != applied_used_slots)
        {
            applied_used_slots = used_slots;
            mat.SetShaderParameter(sn_used_slots, used_slots);
        }
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
        if (state == State.IDLE && queue.Count > 0 && (urgent_queued || (tex_slots_overflowed && TEXTURE_OVERFLOW_SKIP_THROTTLE) ||Time.GetTicksMsec() - last_read_ms >= THROTTLE_MS))
            RenderNextBatch();
    }


    private void RenderNextBatch()
    {
        Span<float> q = MemoryMarshal.Cast<byte, float>(query_bytes);
        int used = 0, n = 0, used_slots = 0;
        tex_slots_overflowed = false;

        while (n < queue.Count)
        {
            var req = queue[n];
            if (used + req.Count > MAX_BATCH_COUNT)
                break;

            int new_used_slots = AssignSlots(req, used_slots);
            if (new_used_slots < 0)
            {
                tex_slots_overflowed = true;  // out of texture slots, the rest goes into a batch right after this one
                break;
            }
            used_slots = new_used_slots;

            int[] data = req.pending_points;
            int[] slot_map = req.slot_map;
            int base_i = used * 4;
            for (int i = 0; i < req.Count * 4; i += 4)
            {
                q[base_i + i    ] = data[i];
                q[base_i + i + 1] = data[i + 1];
                q[base_i + i + 2] = data[i + 2];
                q[base_i + i + 3] = slot_map[data[i + 3]];
            }
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
            q[i + 3] = -1;  // padding, skipped by the shader
        }

        ApplySlots(used_slots);

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
                req.StoreReadbackResults(new ReadOnlySpan<byte>(raw, head, count), q.Slice(head * 4, count * 4), applied_textures);

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
