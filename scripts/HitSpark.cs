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
    [Export] public int ParticleCount { get; set; } = 32;
    [Export] public float LifetimeS { get; set; } = 0.4f;
    [Export] public float SpeedMinMs { get; set; } = 60f;
    [Export] public float SpeedMaxMs { get; set; } = 200f;
    [Export] public float ParticleRadiusM { get; set; } = 0.15f;

    // Called by Projectile.cs. Adds the spark to projectileParent so it survives
    // the projectile's QueueFree. GlobalPosition is set before _Ready runs so
    // the effect appears at the correct world location.
    public static void Spawn(Node projectileParent, Vector3 worldPos)
    {
        var spark = new HitSpark();
        projectileParent.AddChild(spark);
        spark.GlobalPosition = worldPos;
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
            // Full-sphere spread: no orientation logic needed, sparks fly in all
            // directions from the impact point. At 50–800 m combat range this reads
            // clearly as a radial impact burst. A tighter hemisphere would need the
            // surface normal to orient correctly; the full sphere is simpler and
            // physically reasonable for a fragmentation burst in vacuum.
            Spread = 180f,
            Gravity = Vector3.Zero,        // VACUUM — no gravity, no drag
            InitialVelocityMin = SpeedMinMs,
            InitialVelocityMax = SpeedMaxMs,
            ScaleMin = 0.5f,
            ScaleMax = 1.5f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point,
            ColorRamp = rampTex,
        };

        var burst = new GpuParticles3D
        {
            Amount = ParticleCount,
            Lifetime = LifetimeS,
            OneShot = true,
            Explosiveness = 1f,   // emit all particles simultaneously — burst, not stream
            Emitting = true,
            DrawPass1 = mesh,
            ProcessMaterial = procMat,
        };
        AddChild(burst);

        // Lifetime + small buffer so all particles fully fade before the node is freed.
        var timer = GetTree().CreateTimer(LifetimeS + 0.25);
        timer.Timeout += QueueFree;
    }
}
