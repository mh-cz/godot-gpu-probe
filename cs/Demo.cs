using Godot;


public partial class Demo : Node2D
{
    private TextureRect tex_rect;
    private Label info;
    private float draw_scale = 4f;
    private GPUProbe.Request req;

    public override void _Ready()
    {
        tex_rect = GetNode<TextureRect>("UI/View");
        info = GetNode<Label>("UI/Info");

        var tex = GD.Load<Texture2D>("res://icon.svg");
        tex_rect.Texture = tex;
        tex_rect.Size = tex.GetSize() * draw_scale;

        req = new GPUProbe.Request(OnProbed);
    }


    public override void _PhysicsProcess(double delta)
    {
        Vector2I px_pos = (Vector2I)(tex_rect.GetLocalMousePosition() / draw_scale).Floor();
        Texture2D tex = tex_rect.Texture;

        req.Submit([
            (px_pos, GPUProbe.Channel.R, tex),
            (px_pos, GPUProbe.Channel.G, tex),
            (px_pos.X, px_pos.Y, GPUProbe.Channel.B, tex),
            (px_pos.X, px_pos.Y, GPUProbe.Channel.A, tex),
        ]);
    }


    private void OnProbed(GPUProbe.Request req)
    {
        var bytes = req.Result;
        info.Text = $"pixel {req.GetResultPoint(0).pos}\nRGBA {bytes[0]} {bytes[1]} {bytes[2]} {bytes[3]}";
    }
}
