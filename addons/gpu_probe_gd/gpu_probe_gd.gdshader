shader_type canvas_item;
render_mode blend_disabled;

#define MAX_TEXTURES 8 // MUST MATCH MAX_TEXTURES in gpu_probe_gd.gd

uniform sampler2D query_tex : filter_nearest; 				// the query texture (MAX_POINTS, 1)
uniform sampler2D textures[MAX_TEXTURES] : filter_nearest; 	// the textures to probe
uniform ivec2 probe_size; 									// the size of the probe texture (MAX_POINTS / 4, 1)
uniform vec4 oob_colors[MAX_TEXTURES]; 						// out-of-bounds colors for each texture

bool outside(ivec2 pos, ivec2 size) {
	return pos.x < 0 || pos.y < 0 || pos.x >= size.x || pos.y >= size.y;
}

// GLSL ES 3.0 (Compatibility renderer) only allows constant indices into sampler
#define READ_SLOT(N) if (q.w == N) { return outside(q.xy, textureSize(textures[N], 0)) ? oob_colors[N][q.z] : texelFetch(textures[N], q.xy, 0)[q.z]; }

float get_reading(int index) {

	ivec4 q = ivec4(texelFetch(query_tex, ivec2(index, 0), 0));
	if (q.z < 0 || q.z > 3) 
		return 0.0; // invalid

	READ_SLOT(0)
	READ_SLOT(1)
	READ_SLOT(2)
	READ_SLOT(3)
	READ_SLOT(4)
	READ_SLOT(5)
	READ_SLOT(6)
	READ_SLOT(7)

	return 0.0; // invalid
}

void fragment() {
	
	ivec2 iuv = ivec2(UV * vec2(probe_size));
	int last_px = probe_size.x * probe_size.y - 1;
	int i = clamp(iuv.x + iuv.y * probe_size.x, 0, last_px) * 4;

	COLOR = vec4(
		get_reading(i),
		get_reading(i + 1),
		get_reading(i + 2),
		get_reading(i + 3)
	);
}
