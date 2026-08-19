shader_type spatial;
render_mode unshaded;

uniform vec4 star_color = vec4(1.0, 0.78, 0.38, 1.0);
uniform float energy = 10.0;
uniform float speed = 0.12;
uniform float detail_scale = 3.5;

void fragment() {
    EMISSION = star_color.rgb * energy;
    ALBEDO   = vec3(0.0);
}
