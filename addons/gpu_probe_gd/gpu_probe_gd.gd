extends CanvasLayer
## Reads single texel values back from GPU textures in batches. Registered as the GPUProbeGD autoload

enum Channel { R = 0, G = 1, B = 2, A = 3 }
enum _State { IDLE, RENDERING, READBACK }

const RequestGD := preload("gpu_probe_request_gd.gd")  # preloaded so the autoload doesn't depend on the global class cache

# minimum time between readbacks in milliseconds to avoid blocking the GPU
var throttle_ms := 100

# frames to wait before reading back on the Compatibility renderer (OpenGL) to avoid blocking the GPU
var compatibility_wait_frames := 2

# skip the throttle if a batch ran out of texture slots so the leftovers get processed right after without waiting
var texture_overflow_skip_throttle := false

# the larger the probe, the more points can be read back in a single batch but the longer the GPU may stall
# 32x2 = 256 points (bytes) per batch, a sweet spot for most use cases
const PROBE_WIDTH := 32
const PROBE_HEIGHT := 2

# do not change
const MAX_TEXTURES_PER_BATCH := 16
const MAX_BATCH_COUNT := PROBE_WIDTH * PROBE_HEIGHT * 4

var _batch_textures:Array[Texture2D] = []
var _applied_textures:Array[Texture2D] = []
var _batch_oob := PackedVector4Array()
var _applied_oob := PackedVector4Array()

var _applied_used_slots := -1
var _empty_slot_tex:Texture2D
var _tex:Texture2D
var _probe_sbv:SubViewport
var _mat:ShaderMaterial

var _queue:Array[RequestGD] = []
var _batch:Array[RequestGD] = []

var _query := PackedFloat32Array()
var _query_img:Image
var _query_tex:ImageTexture

var _state := _State.IDLE
var _last_read_ms := 0
var _render_frame := 0
var _urgent_queued := false
var _slots_overflowed := false

# Forward+ and Mobile renderers (Vulkan/D3D12/Metal) use asynchronous readback
# Compatibility renderer (OpenGL) uses synchronous immediate readback (_rd == null)
var _rd:RenderingDevice
var _readback_data:PackedByteArray


func _enter_tree() -> void:
	if RequestGD._probe != null and RequestGD._probe != self:
		push_warning("[GPUProbeGD] freeing old instance")
		queue_free()
		return

	RequestGD._probe = self
	process_mode = Node.PROCESS_MODE_ALWAYS


func _exit_tree() -> void:
	if RequestGD._probe == self:
		RequestGD._probe = null


func _ready() -> void:
	if RequestGD._probe != self:
		return

	_probe_sbv = $ProbeViewport
	var rect:ColorRect = $ProbeViewport/QueryRect
	_mat = rect.material

	var probe_size := Vector2i(PROBE_WIDTH, PROBE_HEIGHT)

	_probe_sbv.set_deferred(&"size", probe_size)
	rect.size = probe_size
	_probe_sbv.render_target_update_mode = SubViewport.UPDATE_DISABLED
	_tex = _probe_sbv.get_texture()

	_rd = RenderingServer.get_rendering_device()
	RenderingServer.frame_post_draw.connect(_on_frame_post_draw)

	_query.resize(MAX_BATCH_COUNT * 4)
	_query_img = Image.create_from_data(MAX_BATCH_COUNT, 1, false, Image.FORMAT_RGBAF, _query.to_byte_array())
	_query_tex = ImageTexture.create_from_image(_query_img)

	_mat.set_shader_parameter(&"query_tex", _query_tex)
	_mat.set_shader_parameter(&"probe_size", probe_size)

	_empty_slot_tex = ImageTexture.create_from_image(Image.create_empty(1, 1, false, Image.FORMAT_RGBA8))
	for i in MAX_TEXTURES_PER_BATCH:
		_batch_textures.append(_empty_slot_tex)
	_batch_oob.resize(MAX_TEXTURES_PER_BATCH)
	_apply_slots(0)


func _enqueue_request(req:RequestGD, urgent:bool) -> void:
	if not req._queued:
		req._queued = true
		req._urgent = false
		_queue.append(req)

	if urgent and not req._urgent:
		req._urgent = true
		_queue.erase(req)
		_queue.insert(0, req)
	_urgent_queued = _urgent_queued or urgent


func _cancel_request(req:RequestGD) -> void:
	if req._queued:
		_queue.erase(req)
		req._queued = false
		req._urgent = false
		_urgent_queued = _queue.size() > 0 and _queue[0]._urgent

	req._cancelled = true


func _physics_process(_delta:float) -> void:
	if _state == _State.IDLE and _queue.size() > 0 and (_urgent_queued or (_slots_overflowed and texture_overflow_skip_throttle) or Time.get_ticks_msec() - _last_read_ms >= throttle_ms):
		_render_next_batch()


func _assign_slots(req:RequestGD, used_slots:int) -> int:
	for t in req._textures.size():
		var tex:Texture2D = req._textures[t]
		var rid := tex.get_rid()
		var slot := -1
		for s in used_slots:
			if _batch_oob[s] == req._oob and _batch_textures[s].get_rid() == rid:
				slot = s
				break

		if slot < 0:
			if used_slots == MAX_TEXTURES_PER_BATCH:
				return -1  # slots written past the old count are ignored and cleared by _apply_slots
			slot = used_slots
			_batch_textures[slot] = tex
			_batch_oob[slot] = req._oob
			used_slots += 1

		req._slot_map[t] = slot
	return used_slots


func _render_next_batch() -> void:
	var used := 0
	var n := 0
	var used_slots := 0
	_slots_overflowed = false

	while n < _queue.size():
		var req := _queue[n]
		if used + req.count > MAX_BATCH_COUNT:
			break

		var new_used_slots := _assign_slots(req, used_slots)
		if new_used_slots < 0:
			_slots_overflowed = true  # out of texture slots, the rest goes into a batch right after this one
			break
		used_slots = new_used_slots

		var data:PackedInt32Array = req._pending_points
		var slot_map:PackedInt32Array = req._slot_map
		var base_i := used * 4
		for i in range(0, req.count * 4, 4):
			_query[base_i + i    ] = data[i]
			_query[base_i + i + 1] = data[i + 1]
			_query[base_i + i + 2] = data[i + 2]
			_query[base_i + i + 3] = slot_map[data[i + 3]]
		used += req.count

		req._sent_count = req.count
		req._queued = false
		req._urgent = false
		req._cancelled = false
		_batch.append(req)
		n += 1

	if n == 0:
		return
	for i in n:
		_queue.pop_front()

	_urgent_queued = _queue.size() > 0 and _queue[0]._urgent

	for i in range(used * 4, _query.size(), 4):
		_query[i] = -1
		_query[i + 1] = -1
		_query[i + 2] = 0
		_query[i + 3] = -1  # padding, skipped by the shader

	_apply_slots(used_slots)

	_query_img.set_data(MAX_BATCH_COUNT, 1, false, Image.FORMAT_RGBAF, _query.to_byte_array())
	_query_tex.update(_query_img)
	_probe_sbv.render_target_update_mode = SubViewport.UPDATE_ONCE
	_render_frame = Engine.get_frames_drawn()
	_state = _State.RENDERING


func _apply_slots(used_slots:int) -> void:
	for s in range(used_slots, MAX_TEXTURES_PER_BATCH):
		_batch_textures[s] = _empty_slot_tex  # don't keep textures from older batches alive
		_batch_oob[s] = Vector4.ZERO

	if _batch_textures != _applied_textures or _batch_oob != _applied_oob:
		_applied_textures = _batch_textures.duplicate()
		_applied_oob = _batch_oob.duplicate()
		_mat.set_shader_parameter(&"textures", _applied_textures)
		_mat.set_shader_parameter(&"oob_colors", _applied_oob)

	if used_slots != _applied_used_slots:
		_applied_used_slots = used_slots
		_mat.set_shader_parameter(&"used_slots", used_slots)


func _on_frame_post_draw() -> void:
	if _state != _State.RENDERING:
		return

	if _rd != null:
		var rd_tex := RenderingServer.texture_get_rd_texture(_tex.get_rid())
		if rd_tex.is_valid():
			# Forward+ and Mobile (Vulkan/D3D12/Metal)
			var err := _rd.texture_get_data_async(rd_tex, 0, _on_async_readback_done)
			if err == OK:
				_state = _State.READBACK
				return

			push_warning("[GPUProbeGD] texture_get_data_async failed:(%s). using synchronous readback" % error_string(err))

	# Compatibility (OpenGL)
	if Engine.get_frames_drawn() < _render_frame + compatibility_wait_frames:
		return

	_deliver_batch_results(_tex.get_image().get_data())


func _on_async_readback_done(data:PackedByteArray) -> void:
	if _state != _State.READBACK:
		return

	_readback_data = data
	_deliver_async_readback.call_deferred()


func _deliver_async_readback() -> void:
	var data := _readback_data
	_readback_data = PackedByteArray()
	_deliver_batch_results(data)


func _deliver_batch_results(raw:PackedByteArray) -> void:
	_last_read_ms = Time.get_ticks_msec()
	var head := 0

	for req in _batch:
		var freed:bool = not req.callback.is_null() and not req.callback.is_valid()
		if not req._cancelled and not freed:
			req._store_readback_results(raw, head, _query, _applied_textures)
			if req.callback.is_valid():
				req.callback.call(req)

		head += req._sent_count

	_batch.clear()
	_state = _State.IDLE
