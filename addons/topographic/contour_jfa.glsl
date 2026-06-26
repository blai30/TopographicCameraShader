#[compute]
#version 450

// One jump-flood step over contour SEGMENTS. For each texel, among itself and the 8
// neighbors at the given step offset, keep the valid segment nearest to this texel by
// point-to-segment distance. Running this for step = N/2, N/4, ..., 1 propagates the
// nearest contour segment to every texel.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D src; // x0,y0,x1,y1 (UV); x0<0 invalid
layout(rgba32f, set = 0, binding = 1) uniform image2D dst;

layout(push_constant, std430) uniform Params {
	vec2 size;
	float step;
	float pad0;
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

	vec2 self_uv = (vec2(px) + 0.5) / p.size;
	vec4 best = vec4(-1.0);
	float best_d = 1e9;
	int s = int(p.step);

	for (int dy = -1; dy <= 1; dy++) {
		for (int dx = -1; dx <= 1; dx++) {
			ivec2 q = px + ivec2(dx, dy) * s;
			if (q.x < 0 || q.y < 0 || q.x >= int(p.size.x) || q.y >= int(p.size.y)) { continue; }
			vec4 cand = imageLoad(src, q);
			if (cand.x < 0.0) { continue; }
			float d = seg_dist(self_uv, cand);
			if (d < best_d) { best_d = d; best = cand; }
		}
	}

	imageStore(dst, px, best);
}
