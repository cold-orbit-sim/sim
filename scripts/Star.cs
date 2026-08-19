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
        BuildCorona();
        BuildFlares();
    }

    // ── Surface sphere ──────────────────────────────────────────────────────────

    private void BuildSurface()
    {
        var sphere = new SphereMesh();
        sphere.Radius = StarRadiusM;
        sphere.Height = StarRadiusM * 2f;
        sphere.RadialSegments = 64;
        sphere.Rings = 32;

        var inst = new MeshInstance3D();
        inst.Mesh = sphere;

        var shader = GD.Load<Shader>("res://shaders/star_surface.gdshader");
        if (shader != null)
        {
            var mat = new ShaderMaterial();
            mat.Shader = shader;
            mat.SetShaderParameter("star_color",   StarEmissionColor);
            mat.SetShaderParameter("energy",       StarEmissionEnergy);
            mat.SetShaderParameter("speed",        0.12f);
            mat.SetShaderParameter("detail_scale", 3.5f);
            inst.MaterialOverride = mat;
        }
        else
        {
            // Fallback: plain emissive sphere
            var mat = new StandardMaterial3D();
            mat.EmissionEnabled = true;
            mat.Emission = StarEmissionColor;
            mat.EmissionEnergyMultiplier = StarEmissionEnergy;
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            inst.MaterialOverride = mat;
        }

        AddChild(inst);
    }

    // ── Corona: slightly larger sphere, additive fresnel glow ───────────────────

    private void BuildCorona()
    {
        var sphere = new SphereMesh();
        sphere.Radius = StarRadiusM * 1.18f;
        sphere.Height = StarRadiusM * 2.36f;
        sphere.RadialSegments = 48;
        sphere.Rings = 24;

        var shader = GD.Load<Shader>("res://shaders/star_corona.gdshader");
        if (shader == null) return;

        var mat = new ShaderMaterial();
        mat.Shader = shader;
        mat.SetShaderParameter("corona_color",  StarEmissionColor.Lerp(new Color(1f, 1f, 1f), 0.35f));
        mat.SetShaderParameter("corona_energy", StarEmissionEnergy * 0.35f);
        mat.SetShaderParameter("pulse_speed",   0.18f);
        mat.SetShaderParameter("softness",      3.0f);

        var inst = new MeshInstance3D();
        inst.Mesh = sphere;
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
        flareMat.ShadingMode       = BaseMaterial3D.ShadingModeEnum.Unshaded;
        flareMat.Transparency      = BaseMaterial3D.TransparencyEnum.Alpha;
        flareMat.BlendMode         = BaseMaterial3D.BlendModeEnum.Add;
        flareMat.BillboardMode     = BaseMaterial3D.BillboardModeEnum.Enabled;
        flareMat.EmissionEnabled   = true;
        flareMat.Emission          = StarEmissionColor.Lightened(0.3f);
        flareMat.EmissionEnergyMultiplier = 3.5f;
        flareMat.AlbedoColor       = new Color(1f, 1f, 1f, 1f);
        flareMat.AlbedoTexture     = particleTex;

        // Quad mesh sized relative to star radius
        float quadSize = StarRadiusM * 0.14f;
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
        pmat.EmissionSphereRadius = StarRadiusM;
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
