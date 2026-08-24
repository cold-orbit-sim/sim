shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_back;

uniform float throttle : hint_range(0.0, 1.0) = 0.0;
uniform float heat     : hint_range(0.0, 1.0) = 0.5; // 0 = economy/orange, 1 = power/white-hot
uniform vec4 core_color : source_color = vec4(1.0, 1.0, 0.95, 1.0);
uniform vec4 mid_color  : source_color = vec4(1.0, 0.55, 0.15, 1.0);
uniform vec4 tip_color  : source_color = vec4(0.6, 0.08, 0.02, 1.0);

// Cheap value-noise hash — breaks up the cylinder's silhouette so it doesn't
// read as a clean geometric cone. No textures needed, unshaded/additive so
// tiling artifacts are invisible under the blend.
float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

void fragment() {
    // CylinderMesh UV: V=0 at narrow (nozzle) end, V=1 at wide (tip) end.
    // CONFIRM in-editor — if the gradient runs backwards, flip: float t = 1.0 - UV.y;
    float t = UV.y;

    vec3 hot_core = mix(mid_color.rgb, core_color.rgb, heat);
    vec3 col = mix(hot_core, mid_color.rgb, smoothstep(0.0, 0.35, t));
    col = mix(col, tip_color.rgb, smoothstep(0.3, 1.0, t));

    float fade = (1.0 - t) * (1.0 - t); // brightest at nozzle, quadratic falloff toward tip

    // Two octaves of scrolling noise around the plume's circumference + length,
    // so the alpha isn't uniform per-ring — softens the hard cone edge into
    // something closer to a wispy burn than a solid geometric shape.
    vec2 noise_uv = vec2(UV.x * 6.0, UV.y * 3.0 - TIME * 1.4);
    float n = value_noise(noise_uv) * 0.6 + value_noise(noise_uv * 2.3 + 10.0) * 0.4;
    float breakup = mix(0.5, 1.0, n);

    float alpha = fade * throttle * breakup;

    ALBEDO = col;
    ALPHA = clamp(alpha, 0.0, 1.0);
}
