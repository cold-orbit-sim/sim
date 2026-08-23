using Godot;

namespace ColdOrbit.SimCore;

public partial class Star : Node3D
{
    [Export] public float StarRadiusM        { get; set; } = 50000f;
    [Export] public Color StarEmissionColor  { get; set; } = new Color(1.0f, 0.78f, 0.38f);
    [Export] public float StarEmissionEnergy { get; set; } = 10f;

    [Export] public float HeatZoneAltitudeM  { get; set; } = 100000f;
    [Export] public float MaxHeatPerSecond   { get; set; } = 30f;

    public override void _Ready()
    {
        BuildSurface();
        BuildGlow();
        BuildFlares();
    }

    // ── Surface sphere ──────────────────────────────────────────────────────────

    private MeshInstance3D _surfaceMesh;

    private void BuildSurface()
    {
        var sphere = new SphereMesh();
        sphere.Radius = StarRadiusM;
        sphere.Height = StarRadiusM * 2f;
        sphere.RadialSegments = 64;
        sphere.Rings = 32;

        _surfaceMesh = new MeshInstance3D();
        _surfaceMesh.Mesh = sphere;

        GD.Print($"[Star._Ready] Color={StarEmissionColor} Energy={StarEmissionEnergy}");
        var shader = GD.Load<Shader>("res://shaders/star_surface.gdshader");
        GD.Print($"[Star._Ready] shader={(shader != null ? shader.ResourcePath : "NULL")}");
        if (shader != null)
        {
            var mat = new ShaderMaterial();
            mat.Shader = shader;
            mat.SetShaderParameter("star_color",   (Variant)StarEmissionColor);
            mat.SetShaderParameter("energy",       (Variant)StarEmissionEnergy);
            mat.SetShaderParameter("speed",        (Variant)0.12f);
            mat.SetShaderParameter("detail_scale", (Variant)3.5f);
            _surfaceMesh.MaterialOverride = mat;
        }
        else
        {
            var mat = new StandardMaterial3D();
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.AlbedoColor = new Color(1f, 0.78f, 0.38f);
            _surfaceMesh.MaterialOverride = mat;
        }

        AddChild(_surfaceMesh);
    }

    // ── Glow halo: camera-facing billboard with soft radial gradient ────────────

    private void BuildGlow()
    {
        // Procedural radial gradient: bright near star edge, fades outward.
        // Billboard size = 3× star radius; star disc occupies the inner 2/3.
        const int sz = 128;
        var img = Image.Create(sz, sz, false, Image.Format.Rgba8);
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = (x - (sz - 1) * 0.5f) / ((sz - 1) * 0.5f);
            float dy = (y - (sz - 1) * 0.5f) / ((sz - 1) * 0.5f);
            float r  = Mathf.Sqrt(dx * dx + dy * dy);
            // Narrow bright ring right at star edge (r≈0.67) + soft outer diffuse
            float ring    = Mathf.Exp(-60f * (r - 0.67f) * (r - 0.67f));
            float diffuse = Mathf.Pow(Mathf.Clamp(1f - r, 0f, 1f), 2.5f);
            float a       = Mathf.Clamp(ring * 0.9f + diffuse * 0.35f, 0f, 1f);
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        var glowTex = ImageTexture.CreateFromImage(img);

        var mat = new StandardMaterial3D();
        mat.ShadingMode   = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.Transparency  = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.BlendMode     = BaseMaterial3D.BlendModeEnum.Add;
        mat.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
        mat.AlbedoColor   = StarEmissionColor.Lerp(new Color(1f, 1f, 1f), 0.3f);
        mat.AlbedoTexture = glowTex;

        var quad  = new QuadMesh();
        quad.Size = new Vector2(StarRadiusM * 3f, StarRadiusM * 3f);

        var inst = new MeshInstance3D();
        inst.Mesh = quad;
        inst.MaterialOverride = mat;
        inst.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(inst);
    }

    // ── Solar flares: infrequent particle bursts from the surface ───────────────

    private void BuildFlares()
    {
        // Procedural soft-circle glow texture (64×64)
        var img = Image.Create(64, 64, false, Image.Format.Rgba8);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            float dx = (x - 31.5f) / 31.5f;
            float dy = (y - 31.5f) / 31.5f;
            float r  = Mathf.Sqrt(dx * dx + dy * dy);
            float a  = Mathf.Pow(Mathf.Clamp(1f - r, 0f, 1f), 1.6f);
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        var particleTex = ImageTexture.CreateFromImage(img);

        // Flare material: additive billboard, tinted with star color
        var flareMat = new StandardMaterial3D();
        flareMat.ShadingMode   = BaseMaterial3D.ShadingModeEnum.Unshaded;
        flareMat.Transparency  = BaseMaterial3D.TransparencyEnum.Alpha;
        flareMat.BlendMode     = BaseMaterial3D.BlendModeEnum.Add;
        flareMat.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
        flareMat.AlbedoColor   = StarEmissionColor.Lightened(0.25f);
        flareMat.AlbedoTexture = particleTex;

        // Small quad — flares should be bright spots, not large blobs
        float quadSize = StarRadiusM * 0.07f;
        var flareQuad  = new QuadMesh();
        flareQuad.Size = new Vector2(quadSize, quadSize);
        flareQuad.Material = flareMat;

        // Color ramp: transparent → opaque → fade out
        var gradient   = new Gradient();
        gradient.Colors  = new[] {
            new Color(1f, 1f, 1f, 0f),
            new Color(1f, 1f, 1f, 1f),
            new Color(1f, 1f, 1f, 0.75f),
            new Color(1f, 1f, 1f, 0f)
        };
        gradient.Offsets = new[] { 0f, 0.12f, 0.60f, 1.0f };
        var colorRamp  = new GradientTexture1D();
        colorRamp.Gradient = gradient;

        // Process material: emit from star surface, shoot outward
        var pmat = new ParticleProcessMaterial();
        pmat.EmissionShape        = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface;
        pmat.EmissionSphereRadius = StarRadiusM * 1.001f; // just outside surface — prevents far-side Z-fight
        pmat.Direction            = new Vector3(0f, 1f, 0f);
        pmat.Spread               = 180f; // omnidirectional — particles on far side hidden by star mesh
        pmat.InitialVelocityMin   = StarRadiusM * 0.03f;
        pmat.InitialVelocityMax   = StarRadiusM * 0.12f;
        pmat.Gravity              = Vector3.Zero;
        pmat.ScaleMin             = 0.7f;
        pmat.ScaleMax             = 2.2f;
        pmat.ColorRamp            = colorRamp;

        // GpuParticles3D: low count, long lifetime, burst-style for infrequent flares
        var particles         = new GpuParticles3D();
        particles.Amount      = 8;
        particles.Lifetime    = 9.0;
        particles.Explosiveness = 0.88f; // bursts, not continuous drizzle
        particles.SpeedScale  = 0.10;   // ~1 burst every ~10 s on average
        particles.Preprocess  = 0.0;
        particles.ProcessMaterial = pmat;
        particles.DrawPass1   = flareQuad;

        AddChild(particles);
    }

    // ── Heat physics ────────────────────────────────────────────────────────────

    public override void _PhysicsProcess(double delta)
    {
        var ship = SimBus.Instance.PlayerShipNode;
        if (ship == null) return;

        float dist        = GlobalPosition.DistanceTo(ship.GlobalPosition);
        float surfaceDist = dist - StarRadiusM;

        if (surfaceDist < HeatZoneAltitudeM && surfaceDist > 0f)
        {
            float proximity = 1f - (surfaceDist / HeatZoneAltitudeM);
            SimBus.Instance.Propulsion.ExternalHeatRate = proximity * MaxHeatPerSecond;
        }
        else
        {
            SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
        }
    }
}
