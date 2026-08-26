using Godot;

namespace ColdOrbit.SimCore;

// Guided missile with continuous homing to a locked target. One-shot node:
// spawned at fire, steers toward its target each physics tick, detonates on
// contact. Turn rate limits steering so fast/manoeuvring targets can evade —
// intentional, same philosophy as turret projectiles being missable.
//
// Properties must be set before AddChild (same pattern as Projectile.cs).
// Spawned and configured by ShipMesh.LaunchMissile.
public partial class Missile : Node3D
{
    // Set at launch from ShipMesh.MissileTypeTable for the loaded missile type.
    public float ThrustMs2 { get; set; } = 200f;
    public float MaxSpeedMs { get; set; } = 400f;
    public float TurnRateDegPerSec { get; set; } = 120f;
    public float MaxLifetimeS { get; set; } = 10f;
    public float ImpulseEquivalentN { get; set; } = 200000f;
    public bool NonLethal { get; set; } = false;
    public Color TracerColor { get; set; } = new Color(0.2f, 0.8f, 1.0f); // cyan: distinct from turret slugs
    public float VisualRadiusM { get; set; } = 0.5f;

    private Node3D _target;
    private Vector3 _velocity;
    private float _lifetime;

    // Called by ShipMesh after AddChild but before the first _PhysicsProcess.
    public void Launch(Vector3 origin, Vector3 initialVelocity, Node3D target)
    {
        GlobalPosition = origin;
        _velocity = initialVelocity;
        _target = target;
    }

    public override void _Ready()
    {
        var mesh = new SphereMesh
        {
            Radius = VisualRadiusM,
            Height = VisualRadiusM * 2f,
            RadialSegments = 8,
            Rings = 4,
        };
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

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _lifetime += dt;
        if (_lifetime > MaxLifetimeS) { QueueFree(); return; }

        Vector3 currentDir = _velocity.LengthSquared() > 0.01f
            ? _velocity.Normalized()
            : -GlobalTransform.Basis.Z; // Godot forward fallback when nearly stationary

        if (_target != null && IsInstanceValid(_target))
        {
            Vector3 toTarget = (_target.GlobalPosition - GlobalPosition).Normalized();
            float angle = currentDir.AngleTo(toTarget);
            if (angle > 0.001f)
            {
                float maxTurn = Mathf.DegToRad(TurnRateDegPerSec) * dt;
                currentDir = currentDir.Slerp(toTarget, Mathf.Min(maxTurn / angle, 1f)).Normalized();
            }
            else
            {
                currentDir = toTarget;
            }
        }

        float speed = Mathf.Min(_velocity.Length() + ThrustMs2 * dt, MaxSpeedMs);
        _velocity = currentDir * speed;

        Vector3 nextPos = GlobalPosition + _velocity * dt;
        CheckSweptHit(GlobalPosition, nextPos);
        GlobalPosition = nextPos;
    }

    private void CheckSweptHit(Vector3 from, Vector3 to)
    {
        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollisionMask = 1u << 4; // layer 5 "targets" — same as Projectile.cs
        query.CollideWithAreas = true;
        query.CollideWithBodies = true;
        var result = spaceState.IntersectRay(query);
        if (result.Count > 0 && result["collider"].As<Node3D>() is Node3D hit)
            OnHit(hit, (Vector3)result["position"], (Vector3)result["normal"]);
    }

    private void OnHit(Node3D hitNode, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (hitNode is IDamageable damageable)
            damageable.ApplyWeaponDamage(ImpulseEquivalentN, hitPoint, NonLethal);
        HitSpark.Spawn(GetParent(), hitPoint, hitNormal);
        QueueFree();
    }
}
