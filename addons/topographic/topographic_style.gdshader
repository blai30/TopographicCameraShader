shader_type canvas_item;

// Per-pixel hypsometric tint sampled from the shared height buffer over a UV
// window. Contour lines are drawn separately as vector geometry (ContourLayer),
// so this shader only produces the stepped elevation tint. Elevation is the only
// concept: low ground (including underwater) takes the gradient's low colors.

uniform sampler2D height_buffer : filter_linear, repeat_disable;
uniform sampler2D color_ramp : filter_linear, repeat_disable;
uniform float height_min = -40.0;
uniform float height_max = 110.0;
uniform float contour_interval = 10.0;
uniform vec2 window_center = vec2(0.5);
uniform vec2 window_span = vec2(1.0);

// Contour line styling. px_per_uv converts the SDF (distance stored in buffer-UV
// units) to screen pixels using the current zoom, so the line stays a constant screen
// width with no per-pixel gradient (which is what makes flat ground robust).
uniform float px_per_uv = 1.0;
uniform float major_every = 5.0;
uniform float minor_width_px = 1.1;
uniform float major_width_px = 1.9;
uniform float contour_darken = 0.6;

void fragment() {
	vec2 buffer_uv = clamp(window_center + (UV - vec2(0.5)) * window_span, vec2(0.0), vec2(1.0));
	vec4 buf = texture(height_buffer, buffer_uv);
	float h = mix(height_min, height_max, buf.r);
	float span = max(0.0001, height_max - height_min);

	// B is the band-parity-signed SDF distance (UV units) to the nearest contour line:
	// its magnitude is the distance, its sign flips at every line (and only at lines),
	// so the signed field interpolates linearly through each line and its zero crossing
	// is sub-texel accurate (lines stay crisp at high zoom). The line's level is the
	// nearest contour multiple to the local height, so nothing is stored per line.
	float dist_px = abs(buf.b) * px_per_uv;  // screen-px distance to the nearest line
	float level_index = floor(h / contour_interval + 0.5);

	// Anti-aliased hypsometric tint. A band boundary IS a contour line, so the color
	// step is placed and anti-aliased at the line (~1px), not at the faceted per-pixel
	// height threshold, so band edges stay as smooth as the lines and coincide with
	// them. The side (below/above the nearest line) comes from the local height; the
	// level/blend jumps at mid-band cancel, so the fill color stays continuous there.
	float blend = smoothstep(-0.5, 0.5, dist_px * sign(h - level_index * contour_interval));
	float t_below = clamp(((level_index - 0.5) * contour_interval - height_min) / span, 0.0, 1.0);
	float t_above = clamp(((level_index + 0.5) * contour_interval - height_min) / span, 0.0, 1.0);
	vec3 tint = mix(texture(color_ramp, vec2(t_below, 0.5)).rgb,
		texture(color_ramp, vec2(t_above, 0.5)).rgb, blend);

	// Crisp line: solid core with a 1px anti-aliased edge.
	float is_major = step(0.5, 0.5 - abs(mod(level_index, major_every)));
	float width = mix(minor_width_px, major_width_px, is_major);
	float line_a = clamp(width - dist_px + 0.5, 0.0, 1.0);
	float level_norm = clamp((level_index * contour_interval - height_min) / span, 0.0, 1.0);
	vec3 line_col = texture(color_ramp, vec2(level_norm, 0.5)).rgb * contour_darken;

	COLOR = vec4(mix(tint, line_col, line_a), 1.0);
}
