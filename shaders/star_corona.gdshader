shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never;

uniform vec4  corona_color  = vec4(1.0, 0.88, 0.55, 1.0);
uniform float corona_energy = 1.8;
uniform float pulse_speed   = 0.18;
uniform float softness      = 3.0;

void fragment() {
    // cull_front: back faces rendered, normals still outward.
    // At limb: NORMAL ⊥ VIEW → ndotv ≈ 0 → edge ≈ 1 (bright ring).
    // At centre disc: ndotv ≈ 1 → edge ≈ 0 (transparent).
    float ndotv = abs(dot(NORMAL, VIEW));
    float edge  = pow(1.0 - ndotv, softness);
    float pulse = 1.0 + 0.07 * sin(TIME * pulse_speed * 6.2831853);

    // blend_add: ALBEDO is the additive colour contribution.
    // ALPHA controls how much is added.
    ALBEDO = corona_color.rgb * corona_energy * pulse;
    ALPHA  = clamp(edge * 0.9 * pulse, 0.0, 1.0);
}
