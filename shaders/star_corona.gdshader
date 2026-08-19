shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_draw_never;

uniform vec4 corona_color : source_color = vec4(1.0, 0.88, 0.55, 1.0);
uniform float corona_energy : hint_range(0.0, 10.0) = 1.8;
uniform float pulse_speed : hint_range(0.0, 2.0) = 0.18;
uniform float softness : hint_range(1.0, 6.0) = 3.0;

void fragment() {
    // cull_front: we render the far hemisphere, so NORMAL points inward (away from camera).
    // abs(dot) is 1 at the screen centre of the sphere and 0 at the silhouette edge.
    // Invert: edge = brightest, centre = dark. This creates a ring-glow around the star.
    float ndotv  = abs(dot(NORMAL, VIEW));
    float edge   = pow(1.0 - ndotv, softness);
    float pulse  = 1.0 + 0.07 * sin(TIME * pulse_speed * 6.2831853);

    EMISSION = corona_color.rgb * corona_energy * edge * pulse;
    ALBEDO   = vec3(0.0);
    ALPHA    = clamp(edge * 0.9 * pulse, 0.0, 1.0);
}
