# Godot/Redot GPU probe
A C#/GDscript addon that reads the RGBA values of single pixels from a texture without downloading the whole texture from the GPU first.  
It is useful for reading live data from shaders, textures that change in real time, or lighting brightness levels. It is fast enough even for mobile games.  

## How it works
- The probe works in batches. Each point you ask for returns 1 byte (0..255), one color channel of one pixel. One batch can return up to (32*2)*4 = 256 bytes, from all texture slots combined. One request can hold up to 256 points.
- There are 8 texture slots, so one batch can read from up to 8 different textures.
- GPUs have better things to do than sending data back to the CPU all the time, so by default the probe reads at most once every 100 milliseconds (`GPUProbe.THROTTLE_MS`).
- If you submit the same request again while it is still waiting, it is not added twice. Its points are replaced, so only the latest positions get probed in the next batch.
- Forward+ and Mobile renderers read the data back asynchronously. The Compatibility renderer (OpenGL) waits a few frames (`GPUProbe.COMPATIBILITY_WAIT_FRAMES`) and then reads it back directly.


## Installation
1) Copy the "gpu_probe" folder (or "gpu_probe_gd" folder if you don't use C#) from the "addons" folder of the demo project into your own "addons" folder. If you don't have one, create an "addons" folder in root "res://" directory.  
2) Go to Project -> Project Settings -> Plugins -> GPU Probe -> Enable  
3) "Add Autoload" will show up in the output log. This means the GPU Probe autoload was added.  


## Usage
First, tell the probe which texture to read from, and create a request with a callback:
```cs
TextureRect water_rect;
GPUProbe.Request water_req;
int water_tex_slot = 0;
bool in_water = false;

public override void _Ready()
{
    water_rect = GetNode<TextureRect>("WaterSimRect");
    GPUProbe.SetTexture(water_tex_slot, water_rect.Texture);

    water_req = new GPUProbe.Request(OnWaterProbed);
}
```
Then submit the points you need. Each point is a position, a color channel and a texture slot.  
For example, I store the water in the BLUE channel, so I ask for channel B in water_tex_slot:
```cs
public override void _PhysicsProcess(double delta)
{
    Vector2I px_pos = (Vector2I)water_rect.GetLocalMousePosition().Floor();

    water_req.Submit([
        (px_pos, GPUProbe.Channel.B, water_tex_slot)  // Result[0] -> 0..255
    ]);
}

private void OnWaterProbed(GPUProbe.Request req)
{
    in_water = req.Result[0] > 50;  // if the value is over 50 then I'm in the water
}
```
Keep one request for each thing you want to check and submit it again whenever you need new values. A request does not allocate memory after the first submit, as long as the number of points stays the same.


## More options
**Points:** you can write a point as `(x, y, channel, slot)` or `(Vector2I, channel, slot)`, and you can mix both in the same list:
```cs
req.Submit([
    (px_pos, GPUProbe.Channel.R, 0),
    (px_pos.X + 1, px_pos.Y, GPUProbe.Channel.G, 0),
]);
```

**Results:** `req.Result[i]` is the value of the i-th point. `req.GetResultPoint(i)` tells you which point (position, channel, slot) that value was read from. This is useful because the position may have changed while you were waiting for the result.  
The results stay on the request after the callback, but they are replaced by the next result. Copy them if you need to keep the old values.

**Urgent requests:** `req.Submit(points, urgent: true)` skips the throttle and moves the request to the front of the queue.

**Cancel:** `req.Cancel()` removes the request from the queue. If it was already sent to the GPU, its result is thrown away.

**Out of bounds:** points outside the texture return the color you set with `GPUProbe.SetTexture(slot, texture, out_of_bounds_color: new Color(0, 0, 0, 0))`. The default is transparent black.

**Freed nodes:** if the callback is a method of a node that has been freed, the callback is skipped. This works when the callback is a method of the node. It does not work for a lambda that uses local variables.


## GDScript version
The "gpu_probe_gd" addon is the same probe written in pure GDScript. It works the same way, only the names follow GDScript style:
```gdscript
@onready var water_rect: TextureRect = $WaterSimRect
var water_req := GPUProbeRequestGD.new(_on_water_probed)
var water_tex_slot := 0
var in_water := false

func _ready() -> void:
    GPUProbeGD.set_texture(water_tex_slot, water_rect.texture)

func _physics_process(_delta: float) -> void:
    var px_pos := Vector2i(water_rect.get_local_mouse_position().floor())
    water_req.submit([
        [px_pos, GPUProbeGD.Channel.B, water_tex_slot]  # result[0] -> 0..255
    ])

func _on_water_probed(req: GPUProbeRequestGD) -> void:
    in_water = req.result[0] > 50
```
- A point is `[pos, channel, slot]` or `[x, y, channel, slot]`, and you can mix both in one list.
- `req.result` is a `PackedByteArray`. Every result is a new array, so you can keep the old one without copying it.
- `req.get_result_point(i)` returns `[pos, channel, slot]`.
- `req.submit(points, true)` is an urgent request, and `req.cancel()` cancels it.
- `GPUProbeGD.throttle_ms` and `GPUProbeGD.compatibility_wait_frames` change the timing.
- The callback is skipped if its object was freed.
