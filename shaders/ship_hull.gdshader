shader_type spatial;

// ── Colour uniforms ──────────────────────────────────────────────────────────
uniform vec4 base_color    : source_color = vec4(0.18, 0.11, 0.06, 1.0);
uniform vec4 rust_color    : source_color = vec4(0.52, 0.22, 0.06, 1.0);
uniform vec4 grime_color   : source_color = vec4(0.07, 0.05, 0.03, 1.0);
uniform vec4 scratch_color : source_color = vec4(0.30, 0.28, 0.26, 1.0);

// ── Coverage uniforms ────────────────────────────────────────────────────────
uniform float rust_coverage     : hint_range(0.0, 1.0) = 0.45;
uniform float grime_intensity   : hint_range(0.0, 1.0) = 0.35;
uniform float scratch_intensity : hint_range(0.0, 1.0) = 0.55;
uniform float uv_scale          : hint_range(0.5, 8.0)  = 3.0;

// ── Surface uniforms ─────────────────────────────────────────────────────────
uniform float metallic_base    : hint_range(0.0, 1.0) = 0.15;
uniform float metallic_scratch : hint_range(0.0, 1.0) = 0.60;
uniform float roughness_base   : hint_range(0.0, 1.0) = 0.88;
uniform float roughness_scratch: hint_range(0.0, 1.0) = 0.55;
uniform float normal_strength  : hint_range(0.0, 2.0)  = 0.4;

// ── Noise helpers ────────────────────────────────────────────────────────────
float hash2(vec2 p) {
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 17.43);
    return fract(p.x * p.y);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(hash2(i + vec2(0,0)), hash2(i + vec2(1,0)), f.x),
        mix(hash2(i + vec2(0,1)), hash2(i + vec2(1,1)), f.x),
        f.y
    );
}

float fbm(vec2 p, int octaves) {
    float v = 0.0, a = 0.5;
    for (int i = 0; i < octaves; i++) {
        v += a * vnoise(p);
        p *= 2.1;
        a *= 0.5;
    }
    return v;
}

void fragment() {
    vec2 uv = UV * uv_scale;

    // ── Rust patches (low-frequency) ─────────────────────────────────────────
    float rust_mask = fbm(uv * 1.2 + vec2(0.31, 0.77), 4);
    rust_mask = smoothstep(1.0 - rust_coverage, 1.0, rust_mask);

    // ── Grime (very low-frequency, large dark blobs) ──────────────────────────
    float grime_mask = 1.0 - fbm(uv * 0.6 + vec2(0.9, 0.15), 3);
    grime_mask = smoothstep(0.3, 0.8, grime_mask) * grime_intensity;

    // ── Scratches (high-frequency thin lines) ────────────────────────────────
    float scratch_mask_h = step(0.96, fbm(uv * vec2(60.0, 2.0), 2));
    float scratch_mask_v = step(0.96, fbm(uv * vec2(2.0, 60.0) + vec2(5.3, 2.1), 2));
    float scratch_mask   = clamp(scratch_mask_h + scratch_mask_v, 0.0, 1.0) * scratch_intensity;

    // ── Colour composition ───────────────────────────────────────────────────
    vec3 col = base_color.rgb;
    col = mix(col, rust_color.rgb,    rust_mask);
    col = mix(col, grime_color.rgb,   grime_mask);
    col = mix(col, scratch_color.rgb, scratch_mask);
    ALBEDO = col;

    // ── PBR ─────────────────────────────────────────────────────────────────
    METALLIC  = mix(metallic_base,  metallic_scratch,  scratch_mask);
    ROUGHNESS = mix(roughness_base, roughness_scratch, scratch_mask);

    // ── Fake micro-surface normal from FBM derivative ────────────────────────
    float eps  = 0.01;
    float hc   = fbm(uv, 3);
    float hx   = fbm(uv + vec2(eps, 0.0), 3);
    float hy   = fbm(uv + vec2(0.0, eps), 3);
    vec3 n     = normalize(vec3((hc - hx) / eps, (hc - hy) / eps, 1.0 / normal_strength));
    NORMAL_MAP = n * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = normal_strength;
}
