# Godot/Redot GPU probe (addon)
A C#/GDscript addon that reads individual RGBA values of individual pixels from a GPU texture without downloading the whole texture from the GPU first.  
It is useful for reading pixels from shaders and huge textures that change in real time. It is fast enough even for mobile games.  

## How it works
- Bytes get returned in the same sequence they were asked for in the Submit call array.  
- It uses a microscopic subviewport (32x2 by default) to draw and extract the pixels and channels of your choice.  
- The probe works in batches. Each point you ask for returns 1 byte (0..255), one color channel of one pixel. By default one batch can return up to 256 individual values ((32x2)*4 = 256 bytes) from all textures combined. One request can hold up to 256 points.
- Each point says which texture it reads from, so you don't need to register textures anywhere. One request, and one batch, can read from up to 16 different textures. If the waiting requests use more, the rest go into the next batch.  
- GPUs have better things to do than sending data back to the CPU all the time, so by default the probe reads at most once every 100 milliseconds (`GPUProbe.THROTTLE_MS`).
- If you submit the same request again while it is still waiting, it is not added twice. Its points are replaced, so only the latest positions get probed in the next batch.
- Forward+ and Mobile renderers read the data back asynchronously. The Compatibility renderer (OpenGL) waits a few frames (`GPUProbe.COMPATIBILITY_WAIT_FRAMES`) and then reads it back directly.

## Installation
1) Copy the "gpu_probe" folder (or "gpu_probe_gd" folder if you don't use C#) from the "addons" folder of the demo project into your own "addons" folder. If you don't have one, create an "addons" folder in root "res://" directory.  
2) Go to Project -> Project Settings -> Plugins -> GPU Probe -> Enable  
3) "Add Autoload" will show up in the output log. This means the GPU Probe autoload was added.  
  
If you use the C# version of the engine then do not copy the GDscript addon version or the .gd files will bombard your C# IDE with false positive errors.  


## Usage
Create a new request instance with a callback:  
(You can create as many request instances as you need)
```cs
TextureRect water_rect;
GPUProbe.Request water_req;
bool in_water = false;

public override void _Ready()
{
    water_rect = GetNode<TextureRect>("WaterSimRect");
    water_req = new GPUProbe.Request(OnWaterProbed);
}
```
```gdscript
@onready var water_rect:TextureRect = $WaterSimRect
var water_req := GPUProbeRequestGD.new(_on_water_probed)
var in_water := false
```
Then submit the points you need. Each point is a position, a color channel and the texture to read from.  
For example, I store the water in the BLUE channel, so I ask for channel B of the water texture:
```cs
public override void _PhysicsProcess(double delta)
{
    Vector2I px_pos = (Vector2I)water_rect.GetLocalMousePosition().Floor();

    water_req.Submit([
        (px_pos, GPUProbe.Channel.B, water_rect.Texture)  // byte 0
    ]);
}

private void OnWaterProbed(GPUProbe.Request req)
{
    var bytes = req.Result;
    in_water = bytes[0] > 50;  // if the value is over 50 then I'm in the water
}
```
```gdscript
func _physics_process(_delta: float) -> void:
    var px_pos := Vector2i(water_rect.get_local_mouse_position().floor())

    water_req.submit([
        [px_pos, GPUProbeGD.Channel.B, water_rect.texture]  # byte 0
    ])

func _on_water_probed(req: GPUProbeRequestGD) -> void:
    var bytes := req.result
    in_water = bytes[0] > 50  # if the value is over 50 then I'm in the water
```
Keep one request for each thing you want to check and submit it again whenever you need new values. A request does not allocate memory after the first submit, as long as the number of points stays the same.


## More options
**Points:** you can write a point as `(x, y, channel, texture)` or `(Vector2I, channel, texture)`, and you can mix both in the same list. Points in one request can read from different textures:
```cs
req.Submit([
    (px_pos, GPUProbe.Channel.R, water_tex),
    (px_pos.X + 1, px_pos.Y, GPUProbe.Channel.G, light_tex),
]);
```
```gdscript
req.submit([
    [px_pos, GPUProbeGD.Channel.R, water_tex],
    [px_pos.x + 1, px_pos.y, GPUProbeGD.Channel.G, light_tex],
])
```

**Results:** `req.Result[i]` is the value of the i-th point. `req.GetResultPoint(i)` tells you which point (position, channel, texture) that value was read from. This is useful because the position may have changed while you were waiting for the result.  
The results stay on the request after the callback, but they are replaced by the next result. Copy them if you need to keep the old values.

**Urgent requests:** `req.Submit(points, urgent: true)` skips the throttle and moves the request to the front of the queue.

**Texture overflow:** if the waiting requests use more than 16 different textures, the ones that don't fit wait for the next batch. Set `GPUProbe.TEXTURE_OVERFLOW_SKIP_THROTTLE = true` to send that batch right after the current one, without waiting for the throttle. It is off by default.

**Cancel:** `req.Cancel()` removes the request from the queue. If it was already sent to the GPU, its result is thrown away.

**Out of bounds:** points outside their texture return the color you pass to `req.Submit(points, out_of_bounds_color: new Color(0, 0, 0, 0))`. It applies to all points of the request. The default is transparent black.

**Freed nodes:** if the callback is a method of a node that has been freed, the callback is skipped. This works when the callback is a method of the node. It does not work for a lambda that uses local variables.


## GDScript version
The "gpu_probe_gd" addon is the same probe written in pure GDScript. It works the same way, only the names follow GDScript style:
- A point is `[pos, channel, texture]` or `[x, y, channel, texture]`, and you can mix both in one list.
- `req.result` is a `PackedByteArray`. Every result is a new array, so you can keep the old one without copying it.
- `req.get_result_point(i)` returns `[pos, channel, texture]`.
- `req.submit(points, true)` is an urgent request, and `req.cancel()` cancels it.
- `req.submit(points, false, Color(0, 0, 0, 0))` sets the out of bounds color.
- `GPUProbeGD.throttle_ms`, `GPUProbeGD.compatibility_wait_frames` and `GPUProbeGD.texture_overflow_skip_throttle` change the timing.
- The callback is skipped if its object was freed.
