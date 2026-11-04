using Godot;


public partial class Demo : Node2D
{
    private TextureRect tex_rect;
    private Label info;
    private float draw_scale = 4f;
    private GPUProbe.Request req;
    private int tex_slot = 0;

    public override void _Ready()
    {
        tex_rect = GetNode<TextureRect>("UI/View");
        info = GetNode<Label>("UI/Info");

        var tex = GD.Load<Texture2D>("res://icon.svg");
        tex_rect.Texture = tex;
        tex_rect.Size = tex.GetSize() * draw_scale;

        GPUProbe.SetTexture(tex_slot, tex);

        req = new GPUProbe.Request(OnProbed);
    }


    public override void _PhysicsProcess(double delta)
    {
        Vector2I px_pos = (Vector2I)(tex_rect.GetLocalMousePosition() / draw_scale).Floor();

        req.Submit([
            (px_pos, GPUProbe.Channel.R, tex_slot),
            (px_pos, GPUProbe.Channel.G, tex_slot),
            (px_pos.X, px_pos.Y, GPUProbe.Channel.B, tex_slot),
            (px_pos.X, px_pos.Y, GPUProbe.Channel.A, tex_slot),
        ]);
    }


    private void OnProbed(GPUProbe.Request req)
    {
        var bytes = req.Result;
        info.Text = $"pixel {req.GetResultPoint(0).pos}\nRGBA {bytes[0]} {bytes[1]} {bytes[2]} {bytes[3]}";
    }
}
