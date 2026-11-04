extends Node2D

var tex_slot := 0
var draw_scale := 4.0

@onready var tex_rect:TextureRect = $UI/View
@onready var info:Label = $UI/Info

var req := GPUProbeRequestGD.new(_on_probed)


func _ready() -> void:
	var tex:Texture2D = load("res://icon.svg")
	tex_rect.texture = tex
	tex_rect.size = tex.get_size() * draw_scale

	GPUProbeGD.set_texture(tex_slot, tex)


func _physics_process(_delta: float) -> void:
	var px_pos := Vector2i((tex_rect.get_local_mouse_position() / draw_scale).floor())

	req.submit([
		[px_pos, GPUProbeGD.Channel.R, tex_slot],
		[px_pos, GPUProbeGD.Channel.G, tex_slot],
		[px_pos.x, px_pos.y, GPUProbeGD.Channel.B, tex_slot],
		[px_pos.x, px_pos.y, GPUProbeGD.Channel.A, tex_slot],
	])


func _on_probed(r: GPUProbeRequestGD) -> void:
	var bytes := r.result
	info.text = "pixel %s\nRGBA %d %d %d %d" % [r.get_result_point(0)[0], bytes[0], bytes[1], bytes[2], bytes[3]]
