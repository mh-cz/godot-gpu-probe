@tool
extends EditorPlugin

const AUTOLOAD_NAME := "GPUProbeGD"


func _enable_plugin() -> void:
	add_autoload_singleton(AUTOLOAD_NAME, get_script().resource_path.get_base_dir().path_join("gpu_probe_gd.tscn"))


func _disable_plugin() -> void:
	remove_autoload_singleton(AUTOLOAD_NAME)
