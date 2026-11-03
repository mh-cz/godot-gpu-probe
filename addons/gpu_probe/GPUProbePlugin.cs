#if TOOLS
using Godot;

[Tool]
public partial class GPUProbePlugin : EditorPlugin
{
    public override void _EnablePlugin() => AddAutoloadSingleton(GPUProbe.AUTOLOAD_NAME, GPUProbe.SCENE_PATH);
    public override void _DisablePlugin() => RemoveAutoloadSingleton(GPUProbe.AUTOLOAD_NAME);
}
#endif
