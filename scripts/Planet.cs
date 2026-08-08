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
    }

    // Builds an Earth-like equirectangular texture and assigns it to the
    // visual sphere. Only runs once at startup; the scene mesh size matches
    // PlanetRadius regardless of texture.
    private void ApplyEarthTexture()
    {
        var mesh = GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (mesh == null) return;

        var material = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(BuildEarthTexture()),
            Roughness = 0.9f,
        };
        mesh.MaterialOverride = material;
    }

    private static Image BuildEarthTexture()
    {
        const int width = 2048;
        const int height = 1024;
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgb8);

        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // Continent layout: low-frequency 3D noise on the unit sphere so the
        // map is seamless and has no pole/side stretching.
        var landNoise = new FastNoiseLite
        {
            Seed = (int)rng.Randi(),
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency = 1.0f,
            FractalOctaves = 6,
        };

        // Biome detail: mid-frequency noise for forest/desert/rock variation.
        var terrainNoise = new FastNoiseLite
        {
            Seed = (int)rng.Randi(),
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency = 3.0f,
            FractalOctaves = 5,
        };

        // Cloud layer: banded high-frequency noise, brighter over the ocean.
        var cloudNoise = new FastNoiseLite
        {
            Seed = (int)rng.Randi(),
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency = 1.8f,
            FractalOctaves = 4,
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = (float)x / width;
                float v = (float)y / height;
                float lat = (0.5f - v) * Mathf.Pi;  // +π/2 at top (north) to −π/2 south
                float lon = (u - 0.5f) * Mathf.Tau;

                float nx = Mathf.Cos(lat) * Mathf.Cos(lon);
                float ny = Mathf.Sin(lat);
                float nz = Mathf.Cos(lat) * Mathf.Sin(lon);

                float e = landNoise.GetNoise3D(nx, ny, nz);     // −1..1, continents
                float t = terrainNoise.GetNoise3D(nx, ny, nz);  // biome detail
                float c = cloudNoise.GetNoise3D(nx, ny, nz);    // clouds

                Color col;
                if (e < 0f)
                {
                    // Ocean: deeper → darker blue, lighter toward the coasts.
                    float depth = Mathf.Clamp(-e, 0f, 1f);
                    col = new Color(0.05f, 0.13f, 0.30f)
                        .Lerp(new Color(0.13f, 0.32f, 0.52f), 1f - depth);
                }
                else
                {
                    // Land: green base, tan desert bands, dark forest bands,
                    // rock then snow on the highest terrain.
                    float heightT = Mathf.Clamp(e, 0f, 1f);
                    Color land = new Color(0.30f, 0.48f, 0.26f);
                    float dry = Mathf.Clamp(t * 1.2f + 0.1f, 0f, 1f);
                    land = land.Lerp(new Color(0.68f, 0.56f, 0.36f), dry * 0.8f);
                    float forest = Mathf.Clamp(-t * 1.2f - 0.2f, 0f, 1f);
                    land = land.Lerp(new Color(0.12f, 0.26f, 0.14f), forest * 0.6f);
                    float rock = Mathf.Clamp((heightT - 0.72f) / 0.2f, 0f, 1f);
                    land = land.Lerp(new Color(0.50f, 0.42f, 0.34f), rock);
                    float snow = Mathf.Clamp((heightT - 0.92f) / 0.08f, 0f, 1f);
                    land = land.Lerp(new Color(0.95f, 0.95f, 0.97f), snow);
                    col = land;
                }

                // Polar ice caps beyond ~66° latitude, strongest at the poles.
                float pole = Mathf.Abs(lat);
                if (pole > 1.15f)
                {
                    float cap = Mathf.Clamp((pole - 1.15f) / (Mathf.Pi / 2f - 1.15f), 0f, 1f);
                    col = col.Lerp(new Color(0.93f, 0.94f, 0.98f), cap);
                }

                // Clouds, slightly denser over the ocean.
                float cloudMask = Mathf.Clamp((c - 0.10f) * 1.8f, 0f, 0.85f);
                if (e < 0f) cloudMask = Mathf.Min(cloudMask + 0.10f, 0.9f);
                col = col.Lerp(new Color(0.95f, 0.96f, 0.99f), cloudMask);

                image.SetPixel(x, y, col);
            }
        }

        return image;
    }
}
