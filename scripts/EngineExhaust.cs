using Godot;

namespace ColdOrbit.SimCore;

// Which side of the ship this nozzle is on, used for differential firing during
// a turn (see _Process). Determined by ShipMesh from nozzle geometry at spawn
// time — Center never differential-fires, only suppresses/resumes with the ship.
public enum EngineSide { Left, Center, Right }

// Vacuum-correct exhaust particles at one engine nozzle: a sparse idle ember
// trickle plus a dense, tight-spread "core flame" cluster that reads as a cone
// under real thrust. Driven by SimBus.Instance.Propulsion telemetry (global
// throttle/mix — see batch 22 handback for why per-engine differential thrust
// itself isn't wired; this only differentiates which nozzles are *shown*
// firing, not the underlying physics).
public partial class EngineExhaust : Node3D
{
    [Export] public EngineSide Side = EngineSide.Center;

    [Export] public float IdleParticleFraction = 0.03f;    // sparse idle ember trickle, even at throttle=0
    [Export] public int MaxParticles = 48;
    [Export] public float ParticleBaseSpeed = 40f;
    [Export] public float ParticleMaxSpeedBoost = 160f;

    // Dense "core flame" particles — a second, much busier emitter packed tight
    // against the nozzle. Overlapping additive particles is what sells a "cone of
    // flame" — still zero gravity/drag, still straight lines, still vacuum-correct,
    // just a lot more of them close to the nozzle.
    [Export] public int MaxCoreParticles = 160;
    [Export] public float CoreParticleBaseSpeed = 22f;
    [Export] public float CoreParticleMaxSpeedBoost = 70f;

    // While actively yawing, the firing-side nozzle shows at least this much
    // throttle so the turn reads visually even if linear throttle is at zero
    // (pure rotate-in-place).
    [Export] public float YawFireLevel = 0.6f;

    // How fast particles ease toward their target level, in 1/s (higher = snappier).
    // Shared by spool-up and spool-down so a throttle cut, a yaw release, or a
    // propulsion-disable all decay naturally instead of snapping to zero.
    [Export] public float ResponseRate = 3f;

    private GpuParticles3D _emberParticles;
    private ParticleProcessMaterial _emberProcMat;
    private GpuParticles3D _coreParticles;
    private ParticleProcessMaterial _coreProcMat;

    private float _smoothedParticle;
    private float _smoothedCore;

    public override void _Ready()
    {
        BuildEmberParticles();
        BuildCoreParticles();
        BuildDebugGlowMarker();
    }

    // TEMPORARY diagnostic — a plain bright sphere floating well clear of the hull
    // (20m aft, past all the particle activity), using a fresh material completely
    // unrelated to the GLB import chain. If this doesn't glow either, the problem is
    // something scene/renderer-wide, not specific to engine_core's material or
    // position. If it DOES glow, engine_core's own disc is being blocked by
    // something geometric (recessed inside the nozzle bell, hull overlap, etc.) and
    // the fix is positional, not a material bug at all. Remove once diagnosed.
    private void BuildDebugGlowMarker()
    {
        var mesh = new SphereMesh { Radius = 1.0f, Height = 2.0f };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1.0f, 0.45f, 0.12f),
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.45f, 0.12f),
            EmissionEnergyMultiplier = 6.0f,
        };
        mesh.Material = mat;

        var marker = new MeshInstance3D
        {
            Mesh = mesh,
            Position = new Vector3(0, 0, 20f)
        };
        AddChild(marker);
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

        // Reverse has no modeled nozzles firing forward, so nothing fires at all —
        // per feedback. Yawing fires only the nozzle on the side opposite the turn
        // (turning left -> right nozzle fires) so the ship visually pivots off that
        // one engine; the other nozzles go fully silent, not just dimmer. Neither
        // gate reflects real per-engine differential thrust in the physics (still
        // global throttle there, per the known gap) — this is a visual-only cue.
        bool fires = running && !prop.ReverseEnabled;
        float throttle = prop.ThrottleInput;

        if (fires && (prop.YawLeftActive || prop.YawRightActive))
        {
            bool shouldFire = (prop.YawLeftActive && Side == EngineSide.Right)
                            || (prop.YawRightActive && Side == EngineSide.Left);
            fires = shouldFire;
            if (shouldFire)
                throttle = Mathf.Max(throttle, YawFireLevel);
        }

        if (!fires) throttle = 0f;

        float targetParticle = fires ? Mathf.Max(throttle * 1.1f, IdleParticleFraction) : 0f;
        float targetCore = fires ? throttle : 0f;

        float k = 1f - Mathf.Exp(-ResponseRate * (float)delta);
        _smoothedParticle = Mathf.Lerp(_smoothedParticle, targetParticle, k);
        _smoothedCore = Mathf.Lerp(_smoothedCore, targetCore, k);

        // Particle density and speed scale with throttle. AmountRatio is the cheap way
        // to vary visible particle count without rebuilding the GPU particle system.
        _emberParticles.AmountRatio = Mathf.Clamp(_smoothedParticle, 0f, 1f);
        _emberProcMat.InitialVelocityMin = ParticleBaseSpeed * (0.5f + 0.5f * _smoothedParticle);
        _emberProcMat.InitialVelocityMax = _emberProcMat.InitialVelocityMin + ParticleMaxSpeedBoost * _smoothedParticle;

        _coreParticles.AmountRatio = Mathf.Clamp(_smoothedCore, 0f, 1f);
        _coreProcMat.InitialVelocityMin = CoreParticleBaseSpeed * (0.5f + 0.5f * _smoothedCore);
        _coreProcMat.InitialVelocityMax = _coreProcMat.InitialVelocityMin + CoreParticleMaxSpeedBoost * _smoothedCore;
    }
}
