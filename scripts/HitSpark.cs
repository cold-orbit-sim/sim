using Godot;

namespace ColdOrbit.SimCore;

// One-shot additive particle burst at a projectile impact point. Vacuum-correct:
// no gravity, no drag, sub-second lifetime. Additive unshaded material uses
// AlbedoColor scaling (not Emission — unreliable on Unshaded StandardMaterial3D
// in this renderer, same invariant as EngineExhaust.cs batch 22).
//
// Spawn via HitSpark.Spawn(). The effect adds itself as a sibling of the
// projectile so it survives the projectile's QueueFree().
public partial class HitSpark : Node3D
{
    // Exposed for tuning in the Remote Inspector during play-test.
    [Export] public int ParticleCount { get; set; } = 64;
    [Export] public float LifetimeS { get; set; } = 0.22f;
    [Export] public float SpeedMinMs { get; set; } = 60f;
    [Export] public float SpeedMaxMs { get; set; } = 200f;
    [Export] public float ParticleRadiusM { get; set; } = 0.15f;

    private GpuParticles3D _burst;

    // Called by Projectile.cs. Adds the spark to projectileParent so it survives
    // the projectile's QueueFree. Position and orientation are set after AddChild
    // (needs to be in the scene tree for GlobalPosition/GlobalBasis to work),
    // then Emit() fires the burst.
    public static void Spawn(Node projectileParent, Vector3 worldPos, Vector3 projectileDir)
    {
        var spark = new HitSpark();
        projectileParent.AddChild(spark);   // _Ready builds particles, Emitting = false
        spark.GlobalPosition = worldPos;
        // Orient so local +Z points along the projectile's travel direction.
        // Particles use Spread = 90° (forward hemisphere) so they don't fly back
        // along the incoming trajectory — they shed forward into the target.
        if (projectileDir.LengthSquared() > 0.01f)
        {
            var up = projectileDir.Abs().IsEqualApprox(Vector3.Up) ? Vector3.Forward : Vector3.Up;
            spark.GlobalBasis = Basis.LookingAt(projectileDir, up, true);
        }
        spark.Emit();
    }

    public void Emit()
    {
        _burst.Emitting = true;
        var timer = GetTree().CreateTimer(LifetimeS + 0.15);
        timer.Timeout += QueueFree;
    }

    public override void _Ready()
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
        };

        var mesh = new SphereMesh
        {
            Radius = ParticleRadiusM,
            Height = ParticleRadiusM * 2f,
            RadialSegments = 6,
            Rings = 3,
        };
        mesh.Material = mat;

        // White-hot core → orange → dim red → transparent. Matches the colour
        // temperature sequence of a real metal-on-metal impact: bright flash,
        // brief orange glow, gone. No smoke ramp — vacuum.
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 1f, 0.9f, 1f));
        ramp.AddPoint(0.2f, new Color(1f, 0.75f, 0.25f, 0.9f));
        ramp.AddPoint(0.6f, new Color(0.9f, 0.25f, 0.05f, 0.5f));
        ramp.AddPoint(1f, new Color(0.3f, 0.05f, 0.0f, 0f));
        var rampTex = new GradientTexture1D { Gradient = ramp };

        var procMat = new ParticleProcessMaterial
        {
            // +Z hemisphere: particles fan forward into the target along the
            // projectile's travel direction. Node is oriented in Spawn() so
            // local +Z = projectile direction before Emitting is set true.
            Direction = new Vector3(0f, 0f, 1f),
            Spread = 90f,
            Gravity = Vector3.Zero,        // VACUUM — no gravity, no drag
            InitialVelocityMin = SpeedMinMs,
            InitialVelocityMax = SpeedMaxMs,
            ScaleMin = 0.5f,
            ScaleMax = 1.5f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point,
            ColorRamp = rampTex,
        };

        _burst = new GpuParticles3D
        {
            Amount = ParticleCount,
            Lifetime = LifetimeS,
            OneShot = true,
            Explosiveness = 1f,   // emit all particles simultaneously — burst, not stream
            Emitting = false,     // held until Spawn() sets position/orientation, then calls Emit()
            DrawPass1 = mesh,
            ProcessMaterial = procMat,
        };
        AddChild(_burst);
    }
}
