using Godot;

namespace ColdOrbit.SimCore;

// Vacuum-correct exhaust plume + ember particles at one engine nozzle.
// Driven by SimBus.Instance.Propulsion telemetry (global throttle/mix — see
// batch 22 handback for why per-engine differential isn't wired yet).
public partial class EngineExhaust : Node3D
{
    [Export] public float MaxPlumeLength = 14f;
    [Export] public float MaxPlumeRadius = 1.6f;
    [Export] public float IdlePlumeFraction = 0.12f;   // visible even at throttle=0 — "engine running" cue
    [Export] public int MaxParticles = 48;
    [Export] public float ParticleBaseSpeed = 40f;
    [Export] public float ParticleMaxSpeedBoost = 160f;

    private MeshInstance3D _plumeCore;
    private ShaderMaterial _plumeMat;
    private GpuParticles3D _emberParticles;
    private ParticleProcessMaterial _emberProcMat;

    public override void _Ready()
    {
        BuildPlumeCore();
        BuildEmberParticles();
    }

    private void BuildPlumeCore()
    {
        var cyl = new CylinderMesh
        {
            TopRadius = 0.05f,
            BottomRadius = MaxPlumeRadius,
            Height = MaxPlumeLength,
            RadialSegments = 12,
            Rings = 1
        };

        _plumeCore = new MeshInstance3D { Mesh = cyl };

        // Orient the cylinder's height axis (local Y) onto local +Z (aft, nozzles face
        // aft per batch 17). Rotating -90 about X sends +Y -> -Z, -Y -> +Z, so the wide
        // "bottom" end lands aft. Translating +Height/2 along Z pulls the narrow "top"
        // end back to the nozzle mount point (local origin).
        // CONFIRM in-editor: if the plume points the wrong way, flip the rotation sign.
        _plumeCore.RotationDegrees = new Vector3(-90, 0, 0);
        _plumeCore.Position = new Vector3(0, 0, MaxPlumeLength / 2f);

        var shader = GD.Load<Shader>("res://shaders/ship_exhaust.gdshader");
        _plumeMat = new ShaderMaterial { Shader = shader };
        _plumeCore.MaterialOverride = _plumeMat;

        AddChild(_plumeCore);
    }

    private void BuildEmberParticles()
    {
        _emberParticles = new GpuParticles3D
        {
            Amount = MaxParticles,
            Lifetime = 1.2,
            Emitting = true,
            Position = Vector3.Zero
        };

        var mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 6, Rings = 3 };
        _emberParticles.DrawPass1 = mesh;

        _emberProcMat = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, 1),      // aft, matches nozzle facing
            Spread = 9f,
            Gravity = Vector3.Zero,                // VACUUM — no gravity, no drag, no air resistance
            InitialVelocityMin = ParticleBaseSpeed,
            InitialVelocityMax = ParticleBaseSpeed + ParticleMaxSpeedBoost,
            ScaleMin = 0.6f,
            ScaleMax = 1.3f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.3f
        };

        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1.0f, 0.95f, 0.85f, 1.0f));
        ramp.AddPoint(0.4f, new Color(1.0f, 0.55f, 0.15f, 0.8f));
        ramp.AddPoint(1.0f, new Color(0.4f, 0.05f, 0.02f, 0.0f));
        var rampTex = new GradientTexture1D { Gradient = ramp };
        _emberProcMat.ColorRamp = rampTex;

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true
        };
        mesh.Material = mat;

        _emberParticles.ProcessMaterial = _emberProcMat;
        _emberParticles.AmountRatio = 0f; // start silent; driven per-frame below

        AddChild(_emberParticles);
    }

    public override void _Process(double delta)
    {
        var prop = SimBus.Instance?.Propulsion;
        if (prop == null) return;

        // NOTE: PropulsionState has no Armed flag (only Hardpoints/Ftl do) — main
        // engines have no arm/disarm state, so "running" is just "not disabled".
        // See batch 22 handback for the field-name deviation from the handover spec.
        bool running = !prop.IsPropulsionDisabled;
        float throttle = running ? prop.ThrottleInput : 0f;
        float mix = prop.PropellantMix; // 0 = Economy, 1 = Power

        // Idle floor: a faint running plume even at zero throttle, so "engine on" reads visually.
        float effectiveThrottle = running ? Mathf.Max(throttle, IdlePlumeFraction) : 0f;

        _plumeMat.SetShaderParameter("throttle", effectiveThrottle);
        _plumeMat.SetShaderParameter("heat", mix);

        // Plume geometry scales with throttle — longer, fatter burn at higher throttle.
        float lengthScale = Mathf.Lerp(0.35f, 1.0f, effectiveThrottle);
        float radiusScale = Mathf.Lerp(0.4f, 1.0f, effectiveThrottle);
        _plumeCore.Scale = new Vector3(radiusScale, radiusScale, lengthScale);

        // Particle density and speed scale with throttle. AmountRatio is the cheap way
        // to vary visible particle count without rebuilding the GPU particle system.
        _emberParticles.AmountRatio = Mathf.Clamp(effectiveThrottle * 1.1f, 0f, 1f);
        _emberProcMat.InitialVelocityMin = ParticleBaseSpeed * (0.5f + 0.5f * effectiveThrottle);
        _emberProcMat.InitialVelocityMax = _emberProcMat.InitialVelocityMin + ParticleMaxSpeedBoost * effectiveThrottle;
    }
}
