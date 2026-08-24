shader_type spatial;

// ── Colour uniforms ──────────────────────────────────────────────────────────
uniform vec4 base_color    : source_color = vec4(0.18, 0.11, 0.06, 1.0);
uniform vec4 rust_color    : source_color = vec4(0.52, 0.22, 0.06, 1.0);
uniform vec4 grime_color   : source_color = vec4(0.07, 0.05, 0.03, 1.0);
uniform vec4 scratch_color : source_color = vec4(0.30, 0.28, 0.26, 1.0);
uniform vec4 pit_color     : source_color = vec4(0.03, 0.02, 0.01, 1.0);
uniform vec4 panel_color   : source_color = vec4(0.05, 0.04, 0.03, 1.0);
uniform vec4 rivet_color   : source_color = vec4(0.35, 0.33, 0.30, 1.0);

// ── Coverage uniforms ────────────────────────────────────────────────────────
uniform float rust_coverage     : hint_range(0.0, 1.0) = 0.45;
uniform float grime_intensity   : hint_range(0.0, 1.0) = 0.35;
uniform float scratch_intensity : hint_range(0.0, 1.0) = 0.55;
uniform float pit_intensity     : hint_range(0.0, 1.0) = 0.35;
uniform float uv_scale          : hint_range(0.5, 8.0)  = 3.0;

// ── Multi-scale detail uniforms ──────────────────────────────────────────────
// Micro layer runs at its own frequency so detail stays sharp regardless of
// how uv_scale is tuned for the macro rust/grime read.
uniform float detail_scale      : hint_range(4.0, 60.0) = 22.0;
uniform float streak_strength   : hint_range(0.0, 4.0)  = 2.2; // vertical rust-run stretch

// ── Panel greeble uniforms ───────────────────────────────────────────────────
uniform float panel_grid_scale  : hint_range(1.0, 16.0) = 5.0;
uniform float panel_line_width  : hint_range(0.0, 0.2)  = 0.03;
uniform float rivet_size        : hint_range(0.0, 0.3)  = 0.09;
uniform float greeble_strength  : hint_range(0.0, 1.0)  = 0.6;

// ── Edge wear ─────────────────────────────────────────────────────────────────
uniform float edge_wear_strength : hint_range(0.0, 1.0) = 0.45;

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

// Cheap domain warp — bends the sample coordinate through a second noise
// field so rust blooms and streaks read as organic, not radially symmetric.
vec2 warp(vec2 p, float amount) {
    float wx = fbm(p + vec2(4.2, 1.3), 3);
    float wy = fbm(p + vec2(9.7, 8.1), 3);
    return p + (vec2(wx, wy) - 0.5) * amount;
}

void fragment() {
    vec2 uv = UV * uv_scale;

    // ── Rust patches (macro, domain-warped, vertically streaked) ─────────────
    vec2 streak_uv = vec2(uv.x, uv.y / max(streak_strength, 0.01));
    vec2 rust_uv   = warp(streak_uv * 1.2 + vec2(0.31, 0.77), 0.6);
    float rust_mask = fbm(rust_uv, 5);
    rust_mask = smoothstep(1.0 - rust_coverage, 1.0, rust_mask);

    // ── Grime (very low-frequency, large dark blobs, pools in recesses) ──────
    float grime_mask = 1.0 - fbm(uv * 0.6 + vec2(0.9, 0.15), 4);
    grime_mask = smoothstep(0.3, 0.8, grime_mask) * grime_intensity;

    // ── Fine pitting / corrosion speckle — independent high frequency ────────
    float pit_mask = fbm(uv * detail_scale * 0.35 + vec2(2.2, 6.4), 3);
    pit_mask = smoothstep(0.55, 0.9, pit_mask) * pit_intensity;
    // Pitting concentrates inside rusted regions rather than uniformly.
    pit_mask *= mix(0.15, 1.0, rust_mask);

    // ── Scratches (high-frequency thin lines, two independent orientations) ──
    float scratch_mask_h = step(0.96, fbm(uv * vec2(60.0, 2.0), 2));
    float scratch_mask_v = step(0.96, fbm(uv * vec2(2.0, 60.0) + vec2(5.3, 2.1), 2));
    float scratch_mask   = clamp(scratch_mask_h + scratch_mask_v, 0.0, 1.0) * scratch_intensity;

    // ── Micro surface grain — breaks up flat colour at close range ───────────
    float grain = fbm(uv * detail_scale, 2) - 0.5;

    // ── Panel seams + rivets (greeble) ────────────────────────────────────────
    vec2 grid_uv  = UV * panel_grid_scale;
    vec2 cell     = fract(grid_uv) - 0.5;
    vec2 cell_id  = floor(grid_uv);
    // jitter each panel's seam position slightly so plates aren't perfectly uniform
    float jitter  = hash2(cell_id) * 0.15 - 0.075;
    vec2 dist_to_edge = abs(cell + jitter);
    float seam_mask = 1.0 - smoothstep(0.5 - panel_line_width, 0.5, max(dist_to_edge.x, dist_to_edge.y));
    seam_mask = (1.0 - seam_mask) * greeble_strength;

    // Rivets sit near panel corners, one per cell, position jittered per-cell.
    vec2 rivet_pos = vec2(hash2(cell_id + 3.1), hash2(cell_id + 7.7)) * 0.6 - 0.3;
    float rivet_dist = length(cell - rivet_pos);
    float rivet_mask = (1.0 - smoothstep(rivet_size * 0.5, rivet_size, rivet_dist)) * greeble_strength;

    // ── Edge wear — corners/edges lose paint/rust down to bare scratched metal ─
    float fresnel = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
    float edge_mask = fresnel * edge_wear_strength;

    // ── Colour composition ───────────────────────────────────────────────────
    vec3 col = base_color.rgb;
    col += grain * 0.04; // micro grain breaks up flat fill
    col = mix(col, rust_color.rgb,    rust_mask);
    col = mix(col, pit_color.rgb,     pit_mask);
    col = mix(col, grime_color.rgb,   grime_mask);
    col = mix(col, panel_color.rgb,   seam_mask);
    col = mix(col, rivet_color.rgb,   rivet_mask);
    col = mix(col, scratch_color.rgb, scratch_mask);
    col = mix(col, scratch_color.rgb, edge_mask);
    ALBEDO = col;

    // ── PBR ─────────────────────────────────────────────────────────────────
    float worn = clamp(scratch_mask + rivet_mask + edge_mask, 0.0, 1.0);
    METALLIC  = mix(metallic_base,  metallic_scratch,  worn);
    ROUGHNESS = mix(roughness_base, roughness_scratch, worn);

    // ── Fake micro-surface normal — blends macro FBM with panel seams ────────
    float eps  = 0.01;
    float hc   = fbm(uv * detail_scale * 0.5, 3) + seam_mask * 1.5 + rivet_mask * 2.0;
    float hx   = fbm(uv * detail_scale * 0.5 + vec2(eps, 0.0), 3);
    float hy   = fbm(uv * detail_scale * 0.5 + vec2(0.0, eps), 3);
    vec3 n     = normalize(vec3((hc - hx) / eps, (hc - hy) / eps, 1.0 / normal_strength));
    NORMAL_MAP = n * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = normal_strength;
}
