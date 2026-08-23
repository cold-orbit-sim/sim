shader_type spatial;
render_mode unshaded;

uniform vec4 star_color    = vec4(1.0, 0.78, 0.38, 1.0);
uniform float energy       = 10.0;
uniform float speed        = 0.12;
uniform float detail_scale = 3.5;

// Local-space sphere direction, interpolated to fragment
varying vec3 v_pos;

// ── Noise helpers ─────────────────────────────────────────────────────────────

float h3(vec3 p) {
    p = fract(p * vec3(127.1, 311.7, 74.7));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = h3(i + vec3(0.0, 0.0, 0.0));
    float n100 = h3(i + vec3(1.0, 0.0, 0.0));
    float n010 = h3(i + vec3(0.0, 1.0, 0.0));
    float n110 = h3(i + vec3(1.0, 1.0, 0.0));
    float n001 = h3(i + vec3(0.0, 0.0, 1.0));
    float n101 = h3(i + vec3(1.0, 0.0, 1.0));
    float n011 = h3(i + vec3(0.0, 1.0, 1.0));
    float n111 = h3(i + vec3(1.0, 1.0, 1.0));
    float nx00 = mix(n000, n100, f.x);
    float nx10 = mix(n010, n110, f.x);
    float nx01 = mix(n001, n101, f.x);
    float nx11 = mix(n011, n111, f.x);
    float nxy0 = mix(nx00, nx10, f.y);
    float nxy1 = mix(nx01, nx11, f.y);
    return mix(nxy0, nxy1, f.z);
}

// Unrolled 5-octave fBm — avoids for-loop integer-cast issues on some drivers
float fbm(vec3 p) {
    float v = 0.0;
    v += 0.5200 * vnoise(p);        p *= 2.0;
    v += 0.2600 * vnoise(p);        p *= 2.0;
    v += 0.1300 * vnoise(p);        p *= 2.0;
    v += 0.0650 * vnoise(p);        p *= 2.0;
    v += 0.0325 * vnoise(p);
    return v;
}

// ── Vertex ────────────────────────────────────────────────────────────────────

void vertex() {
    v_pos = normalize(VERTEX);
}

// ── Fragment ──────────────────────────────────────────────────────────────────

void fragment() {
    float t = TIME * speed;
    vec3 q  = v_pos * detail_scale + vec3(t, t * 0.7, t * 0.55);

    // Domain warp: two separate evaluations to avoid compiler inline ambiguity
    float wx   = fbm(q);
    float wy   = fbm(q + vec3(5.2, 1.3, 3.7));
    vec3  warp = vec3(wx, wy, 0.0);

    float surface = fbm(q + warp * 1.1 + vec3(-t * 0.35, t * 0.25, 0.0));

    // Limb darkening
    float ndotv = max(dot(NORMAL, VIEW), 0.0);
    float limb  = 0.18 + 0.82 * pow(ndotv, 0.32);

    // Granulation and hot-plage layering
    float gran  = smoothstep(0.37, 0.60, surface);
    float plage = smoothstep(0.71, 0.87,
                      fbm(v_pos * detail_scale * 1.5 + vec3(3.1, -1.7, t * 0.3)));

    vec3 base = star_color.rgb;
    vec3 col  = mix(base * 0.38, base * 1.28, gran);
    col       = mix(col, mix(base, vec3(1.0), 0.42) * 1.85, plage * 0.62);
    col      *= limb;

    EMISSION = col * energy;
    ALBEDO   = vec3(0.0);
}
