using Godot;


public partial class Demo : Node2D
{
    private TextureRect tex_rect;
    private Label info;
    private float draw_scale = 4f;

    public override void _Ready()
    {
        tex_rect = GetNode<TextureRect>("UI/View");
        info = GetNode<Label>("UI/Info");

        var tex = GD.Load<Texture2D>("res://icon.svg");
        tex_rect.Texture = tex;
        tex_rect.Size = tex.GetSize() * draw_scale;

        GPUProbe.SetTexture(0, tex, out_of_bounds_color: new Color(0, 0, 0, 0));
    }

    public override void _PhysicsProcess(double delta)
    {
        Vector2I px_pos = (Vector2I)(tex_rect.GetLocalMousePosition() / draw_scale).Floor();

        GPUProbe.ProbePosition(GetInstanceId(), 0,
            [
                (px_pos, GPUProbe.Channel.R, 0),
                (px_pos, GPUProbe.Channel.G, 0),
                (px_pos, GPUProbe.Channel.B, 0),
                (px_pos, GPUProbe.Channel.A, 0),
            ],
            bytes => {
                info.Text = $"pixel {px_pos}\nRGBA {bytes[0]} {bytes[1]} {bytes[2]} {bytes[3]}";
            }
        );

    }
}
