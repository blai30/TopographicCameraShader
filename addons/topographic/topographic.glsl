#[compute]
#version 450

// Topographic post-process. Reads the orthographic top-down camera depth
// buffer, reconstructs world height (linear for an orthographic projection),
// and writes a hypsometric tint plus contour lines into the color image. The
// elevation tint is sampled from a gradient texture. Every Nth contour is
// drawn thicker as a major contour. Background and below-sea-level texels
// become water. Runs at PRE_TRANSPARENT, so a transparent-pass marker drawn
// afterward lands on top untouched.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;
layout(set = 0, binding = 2) uniform sampler2D ramp_tex;

layout(push_constant, std430) uniform Params {
	vec2 size;
	float cam_y;
	float near_plane;
	float far_plane;
	float height_min;
	float height_max;
	float contour_interval;
	float contour_width;
	float sea_level;
	float depth_reversed;
	float major_every;
	float major_width_mult;
	float pad;
} p;

float world_y_at(vec2 uv) {
	float d = texture(depth_tex, uv).r;
	float view_z = (p.depth_reversed > 0.5)
		? mix(p.far_plane, p.near_plane, d)
		: mix(p.near_plane, p.far_plane, d);
	return p.cam_y - view_z;
}

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) {
		return;
	}
	vec2 uv = (vec2(px) + 0.5) / p.size;
	vec3 water = vec3(0.16, 0.34, 0.52);

	float d = texture(depth_tex, uv).r;
	bool is_background = (p.depth_reversed > 0.5) ? (d <= 0.00001) : (d >= 0.99999);
	if (is_background) {
		imageStore(color_image, px, vec4(water, 1.0));
		return;
	}

	float wy = world_y_at(uv);
	if (wy < p.sea_level) {
		imageStore(color_image, px, vec4(water, 1.0));
		return;
	}

	// Step the tint per elevation band, sampled from the gradient texture.
	float span = max(0.0001, p.height_max - p.height_min);
	float band_center = (floor(wy / p.contour_interval) + 0.5) * p.contour_interval;
	float t = clamp((band_center - p.height_min) / span, 0.0, 1.0);
	vec3 col = texture(ramp_tex, vec2(t, 0.5)).rgb;

	// Contour lines at band boundaries, thicker on major contours.
	vec2 texel = 1.0 / p.size;
	float wyx = world_y_at(uv + vec2(texel.x, 0.0));
	float wyy = world_y_at(uv + vec2(0.0, texel.y));
	float grad = max(abs(wyx - wy), abs(wyy - wy)) + 0.00001;
	float band_index = floor(wy / p.contour_interval + 0.5);
	bool is_major = (p.major_every > 0.5) && (abs(mod(band_index, p.major_every)) < 0.5);
	float width = p.contour_width * (is_major ? p.major_width_mult : 1.0);
	float dist_to_line = abs(wy - band_index * p.contour_interval);
	float aa = grad * width;
	float line = 1.0 - smoothstep(0.0, aa, dist_to_line);
	col = mix(col, vec3(0.16, 0.11, 0.07), line);

	imageStore(color_image, px, vec4(col, 1.0));
}
