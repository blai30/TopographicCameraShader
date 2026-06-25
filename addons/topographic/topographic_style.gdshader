shader_type canvas_item;

// Topographic styling applied per consumer at the consumer's own resolution.
// Samples the shared height buffer (R = normalized world height) over a UV
// window and draws a stepped hypsometric tint plus contour lines. Elevation is
// the only concept the shader knows: lower ground (including anything that
// happens to be underwater) simply takes the gradient's low-elevation color.
// There is no water special case.
//
// Contour lines use an analytic height gradient sampled from the buffer rather
// than screen-space derivatives (dFdx/fwidth) of the height. Screen derivatives
// become noise-dominated under magnification (gentle slope, high zoom), which
// breaks lines into dots; an analytic gradient stays stable at any zoom.

uniform sampler2D height_buffer : filter_linear, repeat_disable;
uniform sampler2D color_ramp : filter_linear, repeat_disable;
uniform float height_min = -40.0;
uniform float height_max = 110.0;
uniform float contour_interval = 10.0;
uniform float contour_width_px = 0.6;
uniform float major_every = 5.0;
uniform float major_width_mult = 2.2;

// Contour color mode. When contour_dynamic is true, the line takes the local
// band tint darkened by contour_darken. When false, it uses contour_color.
uniform bool contour_dynamic = true;
uniform vec4 contour_color : source_color = vec4(0.12, 0.09, 0.06, 1.0);
uniform float contour_darken = 0.5;

// Suppress contour lines on near-flat ground (a flat plain has no contours).
// Computed from the stable analytic gradient, so it does not stipple.
uniform float contour_flat_guard = 0.015;

// Gaussian blur sigma (in height-buffer texels) applied to the height before
// contouring, so the line position is smooth. Dense kernel: larger means
// smoother, not sparser.
uniform float height_smooth = 2.0;

// Baseline (in height-buffer texels) over which the analytic gradient is
// measured. Wider means the real slope dominates residual noise on gentle
// terrain, which keeps lines solid instead of dashing; too wide softens fine
// gradient detail.
uniform float gradient_span = 4.0;

uniform vec2 window_center = vec2(0.5);
uniform vec2 window_span = vec2(1.0);

const int SMOOTH_RADIUS = 4;

// Dense Gaussian blur of the normalized height, reconstructed to world units.
float sample_smooth_height(vec2 uv) {
	vec2 texel = 1.0 / vec2(textureSize(height_buffer, 0));
	float sigma = max(height_smooth, 0.001);
	float two_sigma_sq = 2.0 * sigma * sigma;
	float sum = 0.0;
	float wsum = 0.0;
	for (int y = -SMOOTH_RADIUS; y <= SMOOTH_RADIUS; y++) {
		for (int x = -SMOOTH_RADIUS; x <= SMOOTH_RADIUS; x++) {
			float w = exp(-float(x * x + y * y) / two_sigma_sq);
			sum += texture(height_buffer, uv + vec2(float(x), float(y)) * texel).r * w;
			wsum += w;
		}
	}
	return mix(height_min, height_max, sum / wsum);
}

void fragment() {
	vec2 texel = 1.0 / vec2(textureSize(height_buffer, 0));
	vec2 buffer_uv = clamp(window_center + (UV - vec2(0.5)) * window_span, vec2(0.0), vec2(1.0));

	float h = sample_smooth_height(buffer_uv);
	float span = max(0.0001, height_max - height_min);

	// Analytic height gradient (height per buffer-uv) from central differences
	// of the smoothed height. Stable under magnification.
	float go = max(gradient_span, 0.5);
	float hx = sample_smooth_height(buffer_uv + vec2(go * texel.x, 0.0))
		- sample_smooth_height(buffer_uv - vec2(go * texel.x, 0.0));
	float hy = sample_smooth_height(buffer_uv + vec2(0.0, go * texel.y))
		- sample_smooth_height(buffer_uv - vec2(0.0, go * texel.y));
	vec2 grad_uv = vec2(hx, hy) / (2.0 * go * texel);

	// Height change per screen pixel along the gradient. fwidth(UV) is the
	// derivative of the linear screen UV, which is stable (unlike fwidth(h)).
	vec2 dh_dpx = grad_uv * window_span * fwidth(UV);
	float grad_px = max(length(dh_dpx), 1e-7);

	// Stepped hypsometric tint.
	float band_center = (floor(h / contour_interval) + 0.5) * contour_interval;
	float tint_t = clamp((band_center - height_min) / span, 0.0, 1.0);
	vec3 tint = texture(color_ramp, vec2(tint_t, 0.5)).rgb;

	// Contour line: distance to the nearest contour level, in screen pixels.
	float bf = h / contour_interval;
	float dist_h = abs(fract(bf + 0.5) - 0.5) * contour_interval;
	float dist_px = dist_h / grad_px;
	float band_index = floor(bf + 0.5);
	bool is_major = (major_every > 0.5) && (abs(mod(band_index, major_every)) < 0.5);
	float half_width = contour_width_px * (is_major ? major_width_mult : 1.0);
	float line = 1.0 - smoothstep(half_width - 0.5, half_width + 0.5, dist_px);

	// Plateau guard from the stable gradient (no stippling).
	float slope_norm = grad_px / max(window_span.x, 1e-4);
	line *= smoothstep(0.0, contour_flat_guard, slope_norm);

	vec3 line_col = contour_dynamic ? tint * contour_darken : contour_color.rgb;
	vec3 col = mix(tint, line_col, line);
	COLOR = vec4(col, 1.0);
}
