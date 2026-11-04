class_name GPUProbeRequestGD extends RefCounted
## A reusable probe request holding the points to read and the values read back for them.
## Keep one per probing site and submit it again for new values.
## Submitting a request that's already queued replaces its points.

const MAX_POINTS := 256  # Must match MAX_BATCH_COUNT in gpu_probe_gd.gd

static var _probe: Node  # The GPUProbeGD autoload, set when it enters the tree

var callback: Callable 				# Called with this request once the data is read back
var count := 0 						# Number of points set by the last submit
var result := PackedByteArray()  	# One byte (0..255) per point from the last delivery, in the order the points were submitted

var _pending_points := PackedInt32Array()  # (x, y, chn, slot) per point
var _result_points := PackedInt32Array()   # (x, y, chn, slot) of the last delivery
var _sent_count := 0

var _queued := false
var _urgent := false
var _cancelled := false


func _init(on_probed: Callable = Callable()) -> void:
	callback = on_probed


# Queues the points to be read back from the GPU.
# Each point is [pos: Vector2i, chn: GPUProbeGD.Channel, slot: int] or [x: int, y: int, chn: GPUProbeGD.Channel, slot: int]
# and both forms can be mixed in one list
func submit(points: Array, urgent := false) -> void:
	if points.size() > MAX_POINTS:
		push_error("[GPUProbeGD] %d points requested, max per request is %d" % [points.size(), MAX_POINTS])
		return

	for pt in points:
		if not (pt is Array and (pt.size() == 3 or pt.size() == 4)):
			push_error("[GPUProbeGD] point must be [pos, chn, slot] or [x, y, chn, slot], got %s" % [pt])
			return

	_pending_points.resize(points.size() * 4)

	for i in points.size():
		var pt: Array = points[i]
		var o := i * 4
		if pt.size() == 3:
			var pos := Vector2i(pt[0])
			_pending_points[o    ] = pos.x
			_pending_points[o + 1] = pos.y
			_pending_points[o + 2] = pt[1]
			_pending_points[o + 3] = pt[2]
		else:
			_pending_points[o    ] = pt[0]
			_pending_points[o + 1] = pt[1]
			_pending_points[o + 2] = pt[2]
			_pending_points[o + 3] = pt[3]

	count = points.size()
	_get_probe()._enqueue_request(self, urgent)


# Removes the request from the queue and drops the result of its batch in flight, if any
func cancel() -> void:
	_get_probe()._cancel_request(self)


# Returns [pos: Vector2i, chn: GPUProbeGD.Channel, slot: int] of the point that result[i] was read from
func get_result_point(i: int) -> Array:
	if i < 0 or i >= result.size():
		push_error("[GPUProbeGD] result index %d out of range 0..%d" % [i, result.size() - 1])
		return []

	var o := i * 4
	return [Vector2i(_result_points[o], _result_points[o + 1]), _result_points[o + 2], _result_points[o + 3]]


func _store_readback_results(raw: PackedByteArray, head: int, query: PackedFloat32Array) -> void:
	result = raw.slice(head, head + _sent_count)
	_result_points.resize(_sent_count * 4)
	for i in _sent_count * 4:
		_result_points[i] = int(query[head * 4 + i])


static func _get_probe() -> Node:
	if _probe == null:
		push_error("[GPUProbeGD] not running. Enable the GPU Probe GD addon so the autoload can be registered")
	return _probe
