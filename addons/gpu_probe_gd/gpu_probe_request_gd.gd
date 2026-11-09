class_name GPUProbeRequestGD extends RefCounted
## A reusable probe request holding the points to read and the values read back for them.
## Keep one per probing site and submit it again for new values.
## Submitting a request that's already queued replaces its points.

# do not change, must match gpu_probe_gd.gd
const MAX_POINTS := 256
const MAX_TEXTURES_PER_BATCH := 16

static var _probe: Node  # The GPUProbeGD autoload, set when it enters the tree

var callback: Callable 				# Called with this request once the data is read back
var count := 0 						# Number of points set by the last submit
var result := PackedByteArray()  	# One byte (0..255) per point from the last delivery, in the order the points were submitted

var _pending_points := PackedInt32Array()  # (x, y, chn, texture index into _textures) per point
var _textures: Array[Texture2D] = []  # distinct textures of the pending points
var _slot_map := PackedInt32Array()     # texture index -> batch slot, filled when the batch is packed
var _oob := Vector4.ZERO

var _scratch_textures: Array[Texture2D] = []
var _result_points := PackedInt32Array()   # (x, y, chn, batch slot) per point of the last delivery
var _result_slot_textures: Array[Texture2D] = []  # batch slot -> texture of the last delivery
var _sent_count := 0

var _queued := false
var _urgent := false
var _cancelled := false


func _init(on_probed: Callable = Callable()) -> void:
	callback = on_probed


# Queues the points to be read back from the GPU. One request can read from up to MAX_TEXTURES_PER_BATCH (16) different textures.
# Points outside their texture return out_of_bounds_color (default transparent black)
# Each point is [pos: Vector2i, chn: GPUProbeGD.Channel, tex: Texture2D] or [x: int, y: int, chn: GPUProbeGD.Channel, tex: Texture2D]
# and both forms can be mixed in one list
func submit(points: Array, urgent := false, out_of_bounds_color := Color(0, 0, 0, 0)) -> void:
	if points.size() > MAX_POINTS:
		push_error("[GPUProbeGD] %d points requested, max per request is %d" % [points.size(), MAX_POINTS])
		return

	# validate and collect the distinct textures before touching the request, it may already be queued
	_scratch_textures.clear()
	for pt in points:
		if not (pt is Array and (pt.size() == 3 or pt.size() == 4) and pt[-1] is Texture2D):
			push_error("[GPUProbeGD] point must be [pos, chn, texture] or [x, y, chn, texture], got %s" % [pt])
			return
		if _texture_index(_scratch_textures, pt[-1]) < 0:
			push_error("[GPUProbeGD] a request can read from at most %d different textures" % MAX_TEXTURES_PER_BATCH)
			return

	var old := _textures
	_textures = _scratch_textures
	_scratch_textures = old
	_scratch_textures.clear()  # don't keep the old textures alive
	_pending_points.resize(points.size() * 4)

	for i in points.size():
		var pt: Array = points[i]
		var o := i * 4
		if pt.size() == 3:
			var pos := Vector2i(pt[0])
			_pending_points[o    ] = pos.x
			_pending_points[o + 1] = pos.y
			_pending_points[o + 2] = pt[1]
		else:
			_pending_points[o    ] = pt[0]
			_pending_points[o + 1] = pt[1]
			_pending_points[o + 2] = pt[2]

		_pending_points[o + 3] = _texture_index(_textures, pt[-1])

	var c := out_of_bounds_color
	_oob = Vector4(c.r, c.g, c.b, c.a)
	_slot_map.resize(_textures.size())
	count = points.size()
	_get_probe()._enqueue_request(self, urgent)


# Removes the request from the queue and drops the result of its batch in flight, if any
func cancel() -> void:
	_get_probe()._cancel_request(self)


# Returns [pos: Vector2i, chn: GPUProbeGD.Channel, texture: Texture2D] of the point that result[i] was read from
func get_result_point(i: int) -> Array:
	if i < 0 or i >= result.size():
		push_error("[GPUProbeGD] result index %d out of range 0..%d" % [i, result.size() - 1])
		return []

	var o := i * 4
	return [Vector2i(_result_points[o], _result_points[o + 1]), _result_points[o + 2], _result_slot_textures[_result_points[o + 3]]]


# Returns the index of tex in textures, adding it if it's new, or -1 if there are too many textures
static func _texture_index(textures: Array[Texture2D], tex: Texture2D) -> int:
	var rid := tex.get_rid()
	for t in textures.size():
		if textures[t].get_rid() == rid:
			return t
	if textures.size() == MAX_TEXTURES_PER_BATCH:
		return -1
	textures.append(tex)
	return textures.size() - 1


func _store_readback_results(raw: PackedByteArray, head: int, query: PackedFloat32Array, slot_textures: Array[Texture2D]) -> void:
	result = raw.slice(head, head + _sent_count)
	_result_slot_textures.assign(slot_textures)
	_result_points.resize(_sent_count * 4)
	for i in _sent_count * 4:
		_result_points[i] = int(query[head * 4 + i])


static func _get_probe() -> Node:
	if _probe == null:
		push_error("[GPUProbeGD] not running. Enable the GPU Probe GD addon so the autoload can be registered")
	return _probe
