extends Node2D

var draw_scale := 4.0

@onready var tex_rect:TextureRect = $UI/View
@onready var info:Label = $UI/Info

var req := GPUProbeRequestGD.new(_on_probed)


func _ready() -> void:
	var tex:Texture2D = load("res://icon.svg")
	tex_rect.texture = tex
	tex_rect.size = tex.get_size() * draw_scale


func _physics_process(_delta: float) -> void:
	var px_pos := Vector2i((tex_rect.get_local_mouse_position() / draw_scale).floor())
	var tex := tex_rect.texture
	
	req.submit([
		[px_pos, GPUProbeGD.Channel.R, tex],  # byte 0
		[px_pos, GPUProbeGD.Channel.G, tex],  # byte 1
		[px_pos.x, px_pos.y, GPUProbeGD.Channel.B, tex],  # byte 2
		[px_pos.x, px_pos.y, GPUProbeGD.Channel.A, tex],  # byte 3
	])


func _on_probed(r: GPUProbeRequestGD) -> void:
	var bytes := r.result
	info.text = "pixel %s\nRGBA %d %d %d %d" % [r.get_result_point(0)[0], bytes[0], bytes[1], bytes[2], bytes[3]]
