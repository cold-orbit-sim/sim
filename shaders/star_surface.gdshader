shader_type spatial;
render_mode unshaded, cull_back;

uniform vec4 star_color : source_color = vec4(1.0, 0.78, 0.38, 1.0);
uniform float energy : hint_range(0.0, 30.0) = 10.0;
uniform float speed : hint_range(0.0, 2.0) = 0.12;
uniform float detail_scale : hint_range(0.5, 10.0) = 3.5;

varying vec3 v_pos; // model-space sphere direction

// --- Smooth 3-D value noise ---
float h3(vec3 p) {
    p = fract(p * vec3(127.1, 311.7, 74.7));
    p += dot(p, p.zyx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f); // Hermite smoothstep
    return mix(
        mix(mix(h3(i),               h3(i + vec3(1,0,0)), f.x),
            mix(h3(i + vec3(0,1,0)), h3(i + vec3(1,1,0)), f.x), f.y),
        mix(mix(h3(i + vec3(0,0,1)), h3(i + vec3(1,0,1)), f.x),
            mix(h3(i + vec3(0,1,1)), h3(i + vec3(1,1,1)), f.x), f.y), f.z);
}

// Fractal Brownian Motion — 5 octaves
float fbm(vec3 p) {
    float v = 0.0;
    float a = 0.52;
    for (int i = 0; i < 5; i++) {
        v += a * vnoise(p);
        // shift domain each octave to avoid self-similarity
        p = p * 2.07 + vec3(3.71 * float(i) + 1.3,
                            7.31 * float(i) + 0.9,
                            1.17 * float(i) + 5.2);
        a *= 0.5;
    }
    return v;
}

void vertex() {
    v_pos = normalize(VERTEX);
}

void fragment() {
    float t = TIME * speed;

    // Domain-warped FBM: first pass warps the domain for the second pass
    vec3 q = v_pos * detail_scale + vec3(t, t * 0.7, t * 0.55);
    vec3 warp = vec3(
        fbm(q + vec3(0.0, 0.0, 0.0)),
        fbm(q + vec3(5.2, 1.3, 3.7)),
        0.0
    );
    float surface = fbm(q + warp * 1.1 + vec3(-t * 0.35, t * 0.25, 0.0));

    // Limb darkening — realistic solar physics
    float ndotv = max(dot(NORMAL, VIEW), 0.0);
    float limb = 0.18 + 0.82 * pow(ndotv, 0.32);

    // Hot granule cells vs cool inter-granule lanes
    float gran = smoothstep(0.37, 0.60, surface);

    // Active-region plages: infrequent very bright hot spots
    float plage_n = fbm(v_pos * detail_scale * 2.3 + vec3(t * 1.4, -t * 0.6, t * 0.8));
    float plage = smoothstep(0.71, 0.87, plage_n);

    vec3 cool      = star_color.rgb * 0.38;
    vec3 hot       = star_color.rgb * 1.28;
    vec3 super_hot = mix(star_color.rgb, vec3(1.0), 0.42) * 1.85;

    vec3 col = mix(cool, hot, gran);
    col = mix(col, super_hot, plage * 0.62);
    col *= limb;

    EMISSION = col * energy;
    ALBEDO = vec3(0.0);
}
