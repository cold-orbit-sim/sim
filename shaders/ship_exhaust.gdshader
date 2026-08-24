shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_back;

uniform float throttle : hint_range(0.0, 1.0) = 0.0;
uniform float heat     : hint_range(0.0, 1.0) = 0.5; // 0 = economy/orange, 1 = power/white-hot
uniform vec4 core_color : source_color = vec4(1.0, 1.0, 0.95, 1.0);
uniform vec4 mid_color  : source_color = vec4(1.0, 0.55, 0.15, 1.0);
uniform vec4 tip_color  : source_color = vec4(0.6, 0.08, 0.02, 1.0);

void fragment() {
    // CylinderMesh UV: V=0 at narrow (nozzle) end, V=1 at wide (tip) end.
    // CONFIRM in-editor — if the gradient runs backwards, flip: float t = 1.0 - UV.y;
    float t = UV.y;

    vec3 hot_core = mix(mid_color.rgb, core_color.rgb, heat);
    vec3 col = mix(hot_core, mid_color.rgb, smoothstep(0.0, 0.35, t));
    col = mix(col, tip_color.rgb, smoothstep(0.3, 1.0, t));

    float fade = (1.0 - t) * (1.0 - t); // brightest at nozzle, quadratic falloff toward tip
    float alpha = fade * throttle;

    ALBEDO = col;
    ALPHA = clamp(alpha, 0.0, 1.0);
}
