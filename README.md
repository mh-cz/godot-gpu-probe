# Godot/Redot GPU probe
A C# addon that allows to pick coordinates on a texture and return the individual RGBA values without having to upload the entire texture from the GPU first.  
This is useful for checking live data from shaders, real-time changing textures or lighting brightness levels.  

## Usage
First tell the probe where to look:
```
ColorRect water_sim_rect;
int water_tex_slot = 0;
bool in_water = false;

public override void _Ready()
{
    water_sim_rect = GetNode<ColorRect>("WaterSimRect");
    GPUProbe.SetTexture(water_tex_slot, water_sim_rect.Texture);
}
```
Then tell it which values you need and what to do with them in a callback:  
For example I store the water in the BLUE channel so I ask for the channel B in the water_tex_slot to get the value.
```
public override void _PhysicsProcess(double delta)
{
    Vector2I px_pos = (Vector2I)(water_sim_rect.GetLocalMousePosition()).Floor();

    GPUProbe.ProbePosition("WATER CHECK".Hash(), 0,
        [
            (px_pos, GPUProbe.Channel.B, water_tex_slot)  // byte 0 -> 0..255
        ],
        bytes => {
            in_water = bytes[0] > 50;  // if the water level in this pixel is over 50 then I'm in the water
        }
    );
}
```
