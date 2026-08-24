using Godot;

namespace ColdOrbit.SimCore;

// Vacuum-correct exhaust plume + ember particles at one engine nozzle.
// Driven by SimBus.Instance.Propulsion telemetry (global throttle/mix — see
// batch 22 handback for why per-engine differential isn't wired yet).
public partial class EngineExhaust : Node3D
{
    [Export] public float MaxPlumeLength = 14f;
    [Export] public float MaxPlumeRadius = 1.6f;
    [Export] public float IdlePlumeFraction = 0.12f;       // plume visible even at throttle=0 — "engine running" cue
    [Export] public float IdleParticleFraction = 0.03f;    // ~25% of the old idle particle density (was 0.12*1.1)
    [Export] public int MaxParticles = 48;
    [Export] public float ParticleBaseSpeed = 40f;
    [Export] public float ParticleMaxSpeedBoost = 160f;

    // Dense "core flame" particles — a second, much busier emitter packed tight
    // against the nozzle. The translucent shader plume alone reads as a thin
    // membrane; overlapping additive particles is what actually sells a "cone of
    // flame" without faking atmosphere — still zero gravity/drag, still straight
    // lines, still vacuum-correct, just a lot more of them close to the nozzle.
    [Export] public int MaxCoreParticles = 160;
    [Export] public float CoreParticleBaseSpeed = 22f;
    [Export] public float CoreParticleMaxSpeedBoost = 70f;

    // How fast the plume/particles/glow ease toward their target level, in 1/s
    // (higher = snappier). Shared by spool-up and spool-down so a throttle cut
    // or a propulsion-disable both decay naturally instead of snapping to zero.
    [Export] public float ResponseRate = 3f;

    private MeshInstance3D _plumeCore;
    private ShaderMaterial _plumeMat;
    private GpuParticles3D _emberParticles;
    private ParticleProcessMaterial _emberProcMat;
    private GpuParticles3D _coreParticles;
    private ParticleProcessMaterial _coreProcMat;

    private float _smoothedPlume;
    private float _smoothedParticle;
    private float _smoothedCore;

    public override void _Ready()
    {
        BuildPlumeCore();
        BuildEmberParticles();
        BuildCoreParticles();
    }

    private void BuildPlumeCore()
    {
        var cyl = new CylinderMesh
        {
            TopRadius = 0.05f,
            BottomRadius = MaxPlumeRadius,
            Height = MaxPlumeLength,
            RadialSegments = 20,
            Rings = 1,
            // No end caps — a capped cone reads as a solid opaque object, especially
            // the flat wide "tip" cap facing the camera. Leaving it open lets the
            // shader's own alpha falloff (fore-to-aft, plus the Fresnel edge fade)
            // be the only thing defining the shape, instead of a flat hard-edged disc.
            CapTop = false,
            CapBottom = false
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

    private void BuildCoreParticles()
    {
        _coreParticles = new GpuParticles3D
        {
            Amount = MaxCoreParticles,
            Lifetime = 0.36,   // 80% of the original 0.45s, per feedback
            Emitting = true,
            Position = Vector3.Zero
        };

        var mesh = new SphereMesh { Radius = 0.176f, Height = 0.352f, RadialSegments = 6, Rings = 3 }; // 80% of original
        _coreParticles.DrawPass1 = mesh;

        _coreProcMat = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, 1),
            Spread = 6f,                          // tight — keeps the cone shape instead of a spray
            Gravity = Vector3.Zero,                // VACUUM — same invariant as the ember emitter
            InitialVelocityMin = CoreParticleBaseSpeed,
            InitialVelocityMax = CoreParticleBaseSpeed + CoreParticleMaxSpeedBoost,
            ScaleMin = 0.64f,  // 80% of original 0.8
            ScaleMax = 1.28f,  // 80% of original 1.6
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.15f
        };

        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1.0f, 1.0f, 0.9f, 1.0f));
        ramp.AddPoint(0.3f, new Color(1.0f, 0.6f, 0.2f, 0.9f));
        ramp.AddPoint(1.0f, new Color(0.5f, 0.1f, 0.02f, 0.0f));
        var rampTex = new GradientTexture1D { Gradient = ramp };
        _coreProcMat.ColorRamp = rampTex;

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true
        };
        mesh.Material = mat;

        _coreParticles.ProcessMaterial = _coreProcMat;
        _coreParticles.AmountRatio = 0f;

        AddChild(_coreParticles);
    }

    public override void _Process(double delta)
    {
        var prop = SimBus.Instance?.Propulsion;
        if (prop == null) return;

        // NOTE: PropulsionState has no Armed flag (only Hardpoints/Ftl do) — main
        // engines have no arm/disarm state, so "unarmed" here means IsPropulsionDisabled.
        // See batch 22 handback for the field-name deviation from the handover spec.
        bool running = !prop.IsPropulsionDisabled;
        float throttle = running ? prop.ThrottleInput : 0f;
        float mix = prop.PropellantMix; // 0 = Economy, 1 = Power

        // Idle floors: a faint running plume/particle trickle even at throttle=0, so
        // "engine on" reads visually — but the two floors are independent (particles
        // want to be much sparser than the plume looks at idle). Both targets fall to
        // zero the instant the engine is disabled; the smoothing below is what makes
        // that read as a fade instead of a snap.
        float targetPlume = running ? Mathf.Max(throttle, IdlePlumeFraction) : 0f;
        float targetParticle = running ? Mathf.Max(throttle * 1.1f, IdleParticleFraction) : 0f;
        // No idle floor for the dense core particles — they're the "under real thrust"
        // cue, distinct from the sparse idle ember trickle. Zero at throttle=0.
        float targetCore = running ? throttle : 0f;

        float k = 1f - Mathf.Exp(-ResponseRate * (float)delta);
        _smoothedPlume = Mathf.Lerp(_smoothedPlume, targetPlume, k);
        _smoothedParticle = Mathf.Lerp(_smoothedParticle, targetParticle, k);
        _smoothedCore = Mathf.Lerp(_smoothedCore, targetCore, k);

        _plumeMat.SetShaderParameter("throttle", _smoothedPlume);
        _plumeMat.SetShaderParameter("heat", mix);

        // Plume geometry scales with throttle — longer, fatter burn at higher throttle.
        float lengthScale = Mathf.Lerp(0.35f, 1.0f, _smoothedPlume);
        float radiusScale = Mathf.Lerp(0.4f, 1.0f, _smoothedPlume);
        _plumeCore.Scale = new Vector3(radiusScale, radiusScale, lengthScale);

        // Particle density and speed scale with throttle. AmountRatio is the cheap way
        // to vary visible particle count without rebuilding the GPU particle system.
        _emberParticles.AmountRatio = Mathf.Clamp(_smoothedParticle, 0f, 1f);
        _emberProcMat.InitialVelocityMin = ParticleBaseSpeed * (0.5f + 0.5f * _smoothedPlume);
        _emberProcMat.InitialVelocityMax = _emberProcMat.InitialVelocityMin + ParticleMaxSpeedBoost * _smoothedPlume;

        _coreParticles.AmountRatio = Mathf.Clamp(_smoothedCore, 0f, 1f);
        _coreProcMat.InitialVelocityMin = CoreParticleBaseSpeed * (0.5f + 0.5f * _smoothedCore);
        _coreProcMat.InitialVelocityMax = _coreProcMat.InitialVelocityMin + CoreParticleMaxSpeedBoost * _smoothedCore;
    }
}
