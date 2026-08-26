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
    private ParticleProcessMaterial _procMat;
    private Vector3 _projectileDir;

    // Called by Projectile.cs. Pass the surface normal from the ray hit so sparks
    // scatter off the surface (not through it). The normal points away from the
    // target surface toward the incoming projectile — sparks fan out in that
    // hemisphere regardless of the angle of incidence.
    public static void Spawn(Node projectileParent, Vector3 worldPos, Vector3 surfaceNormal)
    {
        var spark = new HitSpark();
        spark._projectileDir = surfaceNormal.Normalized();
        projectileParent.AddChild(spark);   // _Ready builds particles, Emitting = false
        spark.GlobalPosition = worldPos;
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

        _procMat = new ParticleProcessMaterial
        {
            // _projectileDir is assigned before AddChild() so it's valid here.
            // Setting Direction at material-creation time avoids a GPU command
            // ordering race: if set in Emit() just before Emitting=true, the
            // renderer may not have received the updated uniform before the
            // one-shot burst fires.
            // Negated: Godot's particle shader emits in the -Direction hemisphere
            // (confirmed empirically — forward hemisphere requires the opposite sign).
            Direction = _projectileDir.LengthSquared() > 0.01f ? -_projectileDir : Vector3.Back,
            Spread = 5f,
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
            ProcessMaterial = _procMat,
        };
        AddChild(_burst);
    }
}
