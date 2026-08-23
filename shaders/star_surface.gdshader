shader_type spatial;

uniform vec4 star_color    = vec4(1.0, 0.78, 0.38, 1.0);
uniform float energy       = 10.0;
uniform float speed        = 0.12;
uniform float detail_scale = 3.5;

void fragment() {
    ALBEDO = star_color.rgb;
}
