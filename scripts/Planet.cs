using Godot;

namespace ColdOrbit.SimCore;

// Compressed-scale planet for the batch 14 nearby-body work. 1 engine unit
// represents ~1 km of real planetary radius, so a 6000-unit sphere reads as
// a ~6000 km world while keeping all gameplay (orbital approach, surface
// proximity) well within Godot's float-precision budget (<~100k units).
//
// Gravity is infinite inverse-square -- no SOI cutoff. SurfaceGravity is the
// designer-facing knob; GM is derived from it so gravity stays correct at any
// distance without a raw gravitational parameter the designer has to guess.
//
// The surface texture is procedurally baked at startup (same pattern as
// StarfieldSky): FastNoiseLite domain-noise sampled on the unit sphere gives
// seamless, non-stretched continents, then colored into oceans, forest/desert
// biomes, polar caps and a cloud layer. Placeholder quality, clearly Earth-like.
//
// Threading: SurfaceGravity is written from the Godot main thread (admin
// panel via SimBus.AdminSetPlanetGravity, applied in SimBus._Process) and
// read on the physics step (PlayerShip._IntegrateForces via GM). This is
// safe today because the planet is a StaticBody3D that never moves and
// Godot's default physics runs single-threaded. If the planet ever becomes
// dynamic (moving/rotating), or multithreaded physics is enabled, the
// SurfaceGravity write and GM/GlobalPosition reads need proper sync.
public partial class Planet : StaticBody3D
{
    [Export] public float PlanetRadius { get; set; } = 6000f;
    [Export] public float AtmosphereRadius { get; set; } = 7200f;
    [Export] public float SurfaceGravity { get; set; } = 9.8f;
    [Export] public string SoiName { get; set; } = "Kael";

    public float GM => SurfaceGravity * PlanetRadius * PlanetRadius;

    public override void _Ready()
    {
        // Register with the sim bus so the admin panel can reach us (gravity
        // override) and read planet constants without holding a scene ref.
        if (SimBus.Instance != null)
        {
            SimBus.Instance.Planet = this;
        }

        ApplyEarthTexture();
        AddCloudLayer();
    }

    private MeshInstance3D? _cloudMesh;

    public override void _Process(double delta)
    {
        // Slow eastward drift — full revolution in ~50 minutes.
        _cloudMesh?.RotateY((float)delta * 0.002f);
    }

    // Builds a multi-pass Earth-like texture and applies it to the visual sphere.
    // Pass 1: domain-warped FBM heightmap → ocean/land/polar colours.
    // Also derives a Sobel normal map from the heightmap for surface relief.
    // Cloud layer is handled separately by AddCloudLayer (animated shader sphere).
    // Only runs once at startup; scene mesh size matches PlanetRadius regardless.
    private void ApplyEarthTexture()
    {
        var mesh = GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (mesh == null) return;

        float[] heights       = BuildHeightmap();
        float[] detailHeights = BuildDetailHeights(heights);

        var material = new StandardMaterial3D
        {
            AlbedoTexture  = ImageTexture.CreateFromImage(BuildAlbedoImage(heights)),
            NormalEnabled  = true,
            NormalTexture  = ImageTexture.CreateFromImage(BuildNormalImage(detailHeights)),
            NormalScale    = 0.3f,
            Roughness      = 0.6f,
            Metallic       = 0.0f,
        };
        mesh.MaterialOverride = material;
    }

    private const int TexW = 1024;
    private const int TexH = 512;

    // Pass 1 — heightmap.
    // Domain-warped FBM Simplex sampled on the unit sphere (seamless, no
    // pole stretching). Fixed seed so Kael looks the same every session.
    private static float[] BuildHeightmap()
    {
        var heights = new float[TexW * TexH];

        var landNoise = new FastNoiseLite
        {
            Seed                        = 31337,
            NoiseType                   = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType                 = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency                   = 0.7f,
            FractalOctaves              = 5,
            DomainWarpEnabled           = true,
            DomainWarpType              = FastNoiseLite.DomainWarpTypeEnum.Simplex,
            DomainWarpAmplitude         = 0.35f,
            DomainWarpFrequency         = 0.3f,
            DomainWarpFractalType       = FastNoiseLite.DomainWarpFractalTypeEnum.Progressive,
            DomainWarpFractalOctaves    = 3,
        };

        for (int y = 0; y < TexH; y++)
        {
            for (int x = 0; x < TexW; x++)
            {
                float u   = (float)x / TexW;
                float v   = (float)y / TexH;
                float lat = (0.5f - v) * Mathf.Pi;
                float lon = (u - 0.5f) * Mathf.Tau;

                float nx = Mathf.Cos(lat) * Mathf.Cos(lon);
                float ny = Mathf.Sin(lat);
                float nz = Mathf.Cos(lat) * Mathf.Sin(lon);

                heights[y * TexW + x] = landNoise.GetNoise3D(nx, ny, nz);
            }
        }

        return heights;
    }

    // High-frequency detail map for the normal pass only.
    // The continent heightmap (freq 0.7) produces country-scale gradients that
    // look like craters under lighting. This blends in a much higher-frequency
    // noise so the Sobel picks up fine surface texture instead.
    private static float[] BuildDetailHeights(float[] baseHeights)
    {
        var detail = new FastNoiseLite
        {
            Seed              = 77213,
            NoiseType         = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType       = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency         = 5.0f,
            FractalOctaves    = 7,
            FractalLacunarity = 2.0f,
            FractalGain       = 0.5f,
        };

        var combined = new float[TexW * TexH];
        for (int y = 0; y < TexH; y++)
        {
            for (int x = 0; x < TexW; x++)
            {
                float u   = (float)x / TexW;
                float v   = (float)y / TexH;
                float lat = (0.5f - v) * Mathf.Pi;
                float lon = (u - 0.5f) * Mathf.Tau;

                float nx = Mathf.Cos(lat) * Mathf.Cos(lon);
                float ny = Mathf.Sin(lat);
                float nz = Mathf.Cos(lat) * Mathf.Sin(lon);

                int idx = y * TexW + x;
                combined[idx] = baseHeights[idx] * 0.25f + detail.GetNoise3D(nx, ny, nz) * 0.75f;
            }
        }
        return combined;
    }

    // Pass 2 — albedo (no clouds; the animated cloud sphere handles that).
    private static Image BuildAlbedoImage(float[] heights)
    {
        var image = Image.CreateEmpty(TexW, TexH, false, Image.Format.Rgb8);

        var oceanDeep  = new Color(0.039f, 0.165f, 0.290f);  // #0a2a4a
        var oceanCoast = new Color(0.102f, 0.420f, 0.541f);  // #1a6b8a
        var landLow    = new Color(0.227f, 0.420f, 0.165f);  // #3a6b2a
        var landMid    = new Color(0.478f, 0.353f, 0.188f);  // #7a5a30
        var landHigh   = new Color(0.541f, 0.478f, 0.416f);  // #8a7a6a
        var landSnow   = new Color(0.910f, 0.910f, 0.910f);  // #e8e8e8
        var iceColor   = new Color(0.930f, 0.950f, 0.990f);

        for (int y = 0; y < TexH; y++)
        {
            for (int x = 0; x < TexW; x++)
            {
                float u = (float)x / TexW;
                float v = (float)y / TexH;
                float h = heights[y * TexW + x];

                Color col;
                if (h < 0f)
                {
                    float depth = Mathf.Clamp(-h, 0f, 1f);
                    col = oceanCoast.Lerp(oceanDeep, depth);
                }
                else
                {
                    float e = Mathf.Clamp(h, 0f, 1f);
                    Color land;
                    if (e < 0.30f)
                        land = landLow.Lerp(landMid, e / 0.30f);
                    else if (e < 0.60f)
                        land = landMid.Lerp(landHigh, (e - 0.30f) / 0.30f);
                    else
                        land = landHigh.Lerp(landSnow, (e - 0.60f) / 0.40f);
                    col = land;
                }

                // Polar caps: fade to ice within 15% of image top/bottom.
                float poleFactor = 0f;
                if (v < 0.15f)
                    poleFactor = (0.15f - v) / 0.15f;
                else if (v > 0.85f)
                    poleFactor = (v - 0.85f) / 0.15f;
                if (poleFactor > 0f)
                    col = col.Lerp(iceColor, poleFactor);

                // Saturation boost — push chroma up ~25 %, leave hue/value alone.
                col = Color.FromHsv(col.H, Mathf.Min(col.S * 1.25f, 1.0f), col.V);

                image.SetPixel(x, y, col);
            }
        }

        return image;
    }

    // Sobel normal map derived from the heightmap. Wraps horizontally,
    // clamps vertically (matching equirectangular pole behaviour).
    private static Image BuildNormalImage(float[] heights)
    {
        var image = Image.CreateEmpty(TexW, TexH, false, Image.Format.Rgb8);
        const float sobelScale = 0.8f;

        float SampleH(int px, int py)
        {
            int sx = (px + TexW) % TexW;
            int sy = Mathf.Clamp(py, 0, TexH - 1);
            return heights[sy * TexW + sx];
        }

        for (int y = 0; y < TexH; y++)
        {
            for (int x = 0; x < TexW; x++)
            {
                float gx =
                    -SampleH(x-1, y-1) + SampleH(x+1, y-1) +
                    -2f * SampleH(x-1, y) + 2f * SampleH(x+1, y) +
                    -SampleH(x-1, y+1) + SampleH(x+1, y+1);

                float gy =
                    -SampleH(x-1, y-1) - 2f * SampleH(x, y-1) - SampleH(x+1, y-1) +
                     SampleH(x-1, y+1) + 2f * SampleH(x, y+1) + SampleH(x+1, y+1);

                var normal = new Vector3(-gx * sobelScale, -gy * sobelScale, 1f).Normalized();
                var col    = new Color(
                    normal.X * 0.5f + 0.5f,
                    normal.Y * 0.5f + 0.5f,
                    normal.Z * 0.5f + 0.5f
                );
                image.SetPixel(x, y, col);
            }
        }

        return image;
    }

    // Adds a baked RGBA cloud texture on a slightly-larger sphere, rotated
    // each frame for drift. StandardMaterial3D alpha transparency is reliable
    // where a ShaderMaterial blend_mix was not.
    private void AddCloudLayer()
    {
        float coverage = 0.10f + GD.Randf() * 0.10f;  // 0.10–0.20 each session

        var mat = new StandardMaterial3D
        {
            AlbedoTexture   = ImageTexture.CreateFromImage(BuildCloudImage(coverage)),
            Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode     = BaseMaterial3D.ShadingModeEnum.PerVertex,
            DepthDrawMode   = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            CullMode        = BaseMaterial3D.CullModeEnum.Back,
            Roughness       = 0.9f,
            Metallic        = 0.0f,
        };

        var sphereMesh = new SphereMesh
        {
            Radius         = PlanetRadius * 1.020f,
            Height         = PlanetRadius * 2.040f,
            RadialSegments = 64,
            Rings          = 32,
        };

        _cloudMesh = new MeshInstance3D { Mesh = sphereMesh, MaterialOverride = mat };
        AddChild(_cloudMesh);
    }

    private static Image BuildCloudImage(float coverage)
    {
        var image = Image.CreateEmpty(TexW, TexH, false, Image.Format.Rgba8);

        var cloudNoise = new FastNoiseLite
        {
            Seed           = 99421,
            NoiseType      = FastNoiseLite.NoiseTypeEnum.Simplex,
            FractalType    = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency      = 2.8f,
            FractalOctaves = 4,
        };

        // GetNoise3D FBM output clusters ~N(0.5, 0.2) after remap to 0..1.
        // Threshold 0.65 → ~20 % of surface covered; 0.60 → ~30 %.
        // coverage 0.10..0.20 maps onto that range linearly.
        float threshold = 0.70f - coverage * 0.5f;

        for (int y = 0; y < TexH; y++)
        {
            for (int x = 0; x < TexW; x++)
            {
                float u   = (float)x / TexW;
                float v   = (float)y / TexH;
                float lat = (0.5f - v) * Mathf.Pi;
                float lon = (u - 0.5f) * Mathf.Tau;

                float nx = Mathf.Cos(lat) * Mathf.Cos(lon);
                float ny = Mathf.Sin(lat);
                float nz = Mathf.Cos(lat) * Mathf.Sin(lon);

                float c     = cloudNoise.GetNoise3D(nx, ny, nz) * 0.5f + 0.5f;  // 0..1
                float alpha = Mathf.Clamp((c - threshold) / 0.08f, 0f, 1f) * 0.82f;

                // Sparser toward poles.
                float pole = Mathf.Abs(v - 0.5f) * 2.0f;
                alpha *= 1.0f - pole * pole * pole;

                image.SetPixel(x, y, new Color(0.96f, 0.97f, 1.0f, alpha));
            }
        }

        return image;
    }
}
