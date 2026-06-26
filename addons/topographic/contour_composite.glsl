#[compute]
#version 450

// Composite pass. Reads the final jump-flood result (the nearest contour segment) and
// writes a SIGNED point-to-segment distance to the nearest contour line into the height
// buffer B channel, preserving R (height) and G (mask). The sign is which side of the
// line the texel is on (below/above the nearest contour level). A signed field
// interpolates linearly through the line, so the bilinear-sampled zero crossing is
// sub-texel accurate: lines stay crisp and do not dash or oscillate at high zoom.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image; // R,G kept; B written
layout(rgba32f, set = 0, binding = 1) uniform image2D seed_final;  // x0,y0,x1,y1 (UV)

layout(push_constant, std430) uniform Params {
	vec2 size;
	float height_min;
	float height_max;
	float interval;
	float pad0;
	float pad1;
	float pad2;
} p;

float seg_dist(vec2 pt, vec4 seg) {
	vec2 a = seg.xy, b = seg.zw;
	vec2 ab = b - a;
	float t = clamp(dot(pt - a, ab) / max(dot(ab, ab), 1e-12), 0.0, 1.0);
	return distance(pt, a + t * ab);
}

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) { return; }

	vec4 c = imageLoad(color_image, px);
	vec4 seg = imageLoad(seed_final, px);
	float dist = 1e3;
	if (seg.x >= 0.0) {
		dist = seg_dist((vec2(px) + 0.5) / p.size, seg);
	}

	// Sign by band parity (the band index even/odd). Parity is constant within a band
	// and flips at every contour line, so adjacent bands get opposite signs (a clean
	// zero crossing at each line) WITHOUT a sign flip mid-band. Signing by side of the
	// nearest level instead would flip at the mid-band point (where round() jumps),
	// producing a spurious zero crossing -> a phantom line between every pair of lines.
	float h = mix(p.height_min, p.height_max, c.r);
	float parity = mod(floor(h / p.interval), 2.0) < 0.5 ? 1.0 : -1.0;
	float signed_dist = dist * parity;

	imageStore(color_image, px, vec4(c.r, c.g, signed_dist, c.a));
}
