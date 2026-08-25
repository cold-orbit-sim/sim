using Godot;

namespace ColdOrbit.SimCore;

public partial class Projectile : Node3D
{
    [Export] public float SpeedMs { get; set; } = 800f;
    [Export] public float HitRadiusM { get; set; } = 3f;
    [Export] public float MaxLifetimeS { get; set; } = 8f; // safety despawn if it never hits anything
    [Export] public float ImpulseEquivalentN { get; set; } = 80000f;
    [Export] public bool NonLethal { get; set; } = false;

    // Visible tracer mesh: a small bright unshaded sphere, additive-blended so
    // it reads as a glowing round in flight. Not Emission — same "unreliable in
    // this project" reasoning as EngineExhaust.cs's glow marker; brightness
    // comes from AlbedoColor instead.
    [Export] public float VisualRadiusM { get; set; } = 0.4f;
    [Export] public Color TracerColor { get; set; } = new Color(1.0f, 0.85f, 0.4f);

    private Vector3 _direction;
    private float _lifetime = 0f;
    private Node3D _excludeShooter; // don't let a turret hit its own ship on spawn frame

    public override void _Ready()
    {
        var mesh = new SphereMesh { Radius = VisualRadiusM, Height = VisualRadiusM * 2f, RadialSegments = 8, Rings = 4 };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = TracerColor,
        };
        mesh.Material = mat;
        AddChild(new MeshInstance3D { Mesh = mesh });
    }

    public void Launch(Vector3 origin, Vector3 aimPoint, Node3D shooter)
    {
        GlobalPosition = origin;
        _direction = (aimPoint - origin).Normalized();
        _excludeShooter = shooter;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _lifetime += dt;
        if (_lifetime > MaxLifetimeS) { QueueFree(); return; }

        Vector3 nextPosition = GlobalPosition + _direction * SpeedMs * dt;
        // Swept check against the "targets" physics layer — a per-frame point check
        // would miss fast projectiles passing through a target between frames at
        // these speeds/timesteps.
        CheckSweptHit(GlobalPosition, nextPosition);
        GlobalPosition = nextPosition;
    }

    private void CheckSweptHit(Vector3 from, Vector3 to)
    {
        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        // Physics layer 5 ("targets", project.godot layer_names/3d_physics/layer_5)
        // is bit 4 (0-indexed) of the mask -> 1u << 4 = 16. Target.cs's LockVolume
        // is an Area3D on that layer, so both bodies and areas must be queried.
        query.CollisionMask = 1u << 4;
        query.CollideWithAreas = true;
        query.CollideWithBodies = true;
        var result = spaceState.IntersectRay(query);
        if (result.Count > 0 && result["collider"].As<Node3D>() is Node3D hit)
        {
            OnHit(hit, (Vector3)result["position"]);
        }
    }

    private void OnHit(Node3D hitNode, Vector3 hitPoint)
    {
        if (hitNode is IDamageable damageable)
        {
            damageable.ApplyWeaponDamage(ImpulseEquivalentN, hitPoint, NonLethal);
        }
        // If hitNode is NOT IDamageable (e.g. a firing-range Target's LockVolume),
        // nothing happens beyond a visual spark — Target.cs deliberately doesn't
        // implement IDamageable, which is what makes firing-range targets
        // indestructible without any extra guard code.
        SpawnHitSpark(hitPoint);
        QueueFree();
    }

    private void SpawnHitSpark(Vector3 hitPoint)
    {
        // TODO: small one-shot additive particle burst at hitPoint, reusing the
        // additive-unshaded-material technique from EngineExhaust.cs (batch 22).
        // Not built this batch — flagged as a visual-polish TODO in the handback.
    }
}
