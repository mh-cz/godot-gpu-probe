#if TOOLS
using Godot;

[Tool]
public partial class GPUProbePlugin : EditorPlugin
{
    public override void _EnablePlugin() 
    { 
        AddAutoloadSingleton(GPUProbe.AUTOLOAD_NAME, GPUProbe.SCENE_PATH); 
        GD.Print("[GPUProbe] Addon enabled.");
    }
    public override void _DisablePlugin()
    {
        RemoveAutoloadSingleton(GPUProbe.AUTOLOAD_NAME);
        GD.Print("[GPUProbe] Addon disabled.");
    }
}
#endif
