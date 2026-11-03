using System.Collections.Generic;
using Godot;

namespace ColdOrbit.SimCore;

public enum EnemyAiState { Approach, Orbit, Fire, Destroyed }

// First-pass enemy: engage-and-orbit (decision log 2026-08-25). Approach the
// player until inside OrbitRadiusM, hold that range while circling, and fire the
// dorsal turret once it reports Locked. No fleeing, formations or cover.
//
// Damage model is the player's own: a private EngineeringState (the 9-subsystem
// model in SubsystemHealth.cs) fed through the same ApplyDamage formula for both
// weapon hits (IDamageable) and collisions. PlayerShip.cs is untouched; the few
// lines of collision plumbing below mirror PlayerShip.HandleCollision.
//
// Physics layers: this body sits on layers 1 + 6 ("ships") and masks layer 1.
// Projectile rays query layers 5 + 6, so the ray hits this RigidBody3D directly
// and routes into IDamageable below. Target.cs (layer 5, not IDamageable) is
// unaffected.
public partial class EnemyShip : RigidBody3D, IDamageable
{
    // Metadata shaped like Target.cs so the player's turret target_* fields work.
    [Export] public string TargetDisplayName { get; set; } = "Enemy-01";
    [Export] public string TargetClass { get; set; } = "Hostile Cruiser";
    [Export] public string TargetAlliance { get; set; } = "Hostile";

    [Export] public float OrbitRadiusM { get; set; } = 500f;
    [Export] public float OrbitSpeedMs { get; set; } = 30f;       // tangential speed relative to player
    [Export] public float ApproachSpeedMs { get; set; } = 60f;
    [Export] public float ThrustForce { get; set; } = 4000f;      // same as PlayerShip default; Mass 1000 -> 4 m/s^2
    [Export] public float VelocityGain { get; set; } = 1.0f;      // 1/s, how hard it chases its desired velocity
    [Export] public float MaxTurnRateRadPerS { get; set; } = 0.8f;
    [Export] public float TurnGain { get; set; } = 3f;
    [Export] public float OrbitUpTiltDeg { get; set; } = 45f;     // roll so the player sits ~45 deg above the dorsal turret plane
    [Export] public float CollisionBounce { get; set; } = 0.3f;
    [Export] public float CollisionAlertThresholdN { get; set; } = 5000f; // min impulse that deals damage (as PlayerShip)
    [Export] public float DamageScaleN { get; set; } = 50000f;
    [Export] public float ZoneThreshold { get; set; } = 0.5f;
    [Export] public float DestroyedLingerS { get; set; } = 2f;

    public EngineeringState Engineering { get; } = new();
    public EnemyAiState State { get; private set; } = EnemyAiState.Approach;

    private TurretController _turret;
    private float _destroyedTimer = 0f;
    // Written on the physics thread (_IntegrateForces), consumed in _PhysicsProcess.
    private float _pendingCollisionImpulse = 0f;
    private Vector3 _pendingCollisionNormal = Vector3.Zero;

    public override void _Ready()
    {
        ContactMonitor = true;
        MaxContactsReported = 4;
        PhysicsMaterialOverride = new PhysicsMaterial { Bounce = CollisionBounce, Friction = 0f };
        AddToGroup("lockable_targets"); // so the player's turrets can cycle to / lock it
    }

    // Called by ShipMesh._Ready (children ready before parent) with the dorsal turret.
    public void RegisterTurret(TurretController turret) => _turret = turret;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (State == EnemyAiState.Destroyed)
        {
            _destroyedTimer -= dt;
            if (_destroyedTimer <= 0f) QueueFree();
            return;
        }

        HandleCollision();
        if (Engineering.Hull.Disabled) { Destroy(); return; }

        var player = SimBus.Instance?.PlayerShipNode;
        if (player == null || !IsInstanceValid(player)) return;

        Vector3 toShip = GlobalPosition - player.GlobalPosition;
        float dist = toShip.Length();
        if (dist < 0.01f) return;
        Vector3 rHat = toShip / dist; // outward from player

        UpdateTurret(player);
        UpdateState(dist);
        Steer(player, rHat, dist, dt);
    }

    private void UpdateTurret(Node3D player)
    {
        if (_turret == null || !IsInstanceValid(_turret)) return;
        _turret.Armed = true;
        if (!_turret.HasTarget) _turret.SelectTarget(player);

        // Existing fire control does the rest (lock timer, heat, ammo, projectile).
        bool trigger = State == EnemyAiState.Fire && !Engineering.Weapons.Disabled;
        _turret.SetFiring(trigger);
        if (_turret.AmmoRemaining.GetValueOrDefault(_turret.AmmoLoaded, 0) <= 0) _turret.StartReload();
    }

    private void UpdateState(float dist)
    {
        bool locked = _turret != null && _turret.LockState == TurretLockState.Locked;
        EnemyAiState next = State;
        switch (State)
        {
            case EnemyAiState.Approach:
                if (dist <= OrbitRadiusM * 1.05f) next = EnemyAiState.Orbit;
                break;
            case EnemyAiState.Orbit:
                if (dist > OrbitRadiusM * 1.5f) next = EnemyAiState.Approach;
                else if (locked) next = EnemyAiState.Fire;
                break;
            case EnemyAiState.Fire:
                if (dist > OrbitRadiusM * 1.5f) next = EnemyAiState.Approach;
                else if (!locked) next = EnemyAiState.Orbit;
                break;
        }
        if (next == State) return;
        GD.Print($"[EnemyShip {Name}] {State} -> {next} (range {dist:F0} m, lock {_turret?.LockState.ToString() ?? "n/a"})");
        State = next;
    }

    private void Steer(PlayerShip player, Vector3 rHat, float dist, float dt)
    {
        // Engines at 0 health: no thrust or steering, the ship just drifts (same
        // gate PlayerShip.HandleThrust uses).
        if (Engineering.Engines.Disabled) return;

        Vector3 vRel;
        Vector3 forward;
        Vector3 upHint;
        if (State == EnemyAiState.Approach)
        {
            vRel = -rHat * Mathf.Clamp((dist - OrbitRadiusM) * 0.5f, 0f, ApproachSpeedMs);
            forward = -rHat;
            upHint = Vector3.Up;
        }
        else
        {
            Vector3 tangent = Vector3.Up.Cross(rHat);
            if (tangent.LengthSquared() < 0.04f) tangent = Vector3.Right.Cross(rHat); // player directly above/below
            tangent = tangent.Normalized();
            float radial = Mathf.Clamp((dist - OrbitRadiusM) * 0.3f, -15f, 15f); // + = too far, close in
            vRel = tangent * OrbitSpeedMs - rHat * radial;
            forward = tangent;
            Vector3 side = tangent.Cross(rHat).Normalized();
            float tilt = Mathf.DegToRad(OrbitUpTiltDeg);
            upHint = -rHat * Mathf.Cos(tilt) + side * Mathf.Sin(tilt);
        }

        // Thrust: velocity-chase, capped at what the engines can deliver given
        // engine and reactor degradation (same multipliers PlayerShip/Engineering use).
        float maxAccel = ThrustForce / Mass * Engineering.ThrustMultiplier * Engineering.ReactorPowerMultiplier;
        Vector3 accel = (player.LinearVelocity + vRel - LinearVelocity) * VelocityGain;
        if (accel.Length() > maxAccel) accel = accel.Normalized() * maxAccel;
        ApplyCentralForce(accel * Mass);

        // Orientation: slew toward desired basis via angular velocity (stays physical).
        if (Mathf.Abs(forward.Dot(upHint.Normalized())) > 0.99f) upHint = Vector3.Forward;
        Basis desired = Basis.LookingAt(forward, upHint);
        Quaternion q = (desired * GlobalTransform.Basis.Orthonormalized().Inverse()).GetRotationQuaternion();
        if (q.W < 0f) q = -q;
        float angle = q.GetAngle();
        AngularVelocity = angle < 0.001f
            ? Vector3.Zero
            : q.GetAxis() * Mathf.Min(angle * TurnGain, MaxTurnRateRadPerS);
    }

    // IDamageable — same routing as PlayerShip.ApplyWeaponDamage, on this ship's own state.
    public void ApplyWeaponDamage(float impulseEquivalentN, Vector3 hitPointGlobal, bool nonLethal)
    {
        if (State == EnemyAiState.Destroyed) return;
        Vector3 worldNormal = (hitPointGlobal - GlobalPosition).Normalized();
        Engineering.ApplyDamage(impulseEquivalentN, worldNormal, GlobalTransform.Basis, DamageScaleN, ZoneThreshold, nonLethal);
        GD.Print($"[EnemyShip {Name}] hit: {HealthSummary()}");
    }

    // Mirrors the core of PlayerShip.HandleCollision (no alerts/MQTT on an NPC).
    private void HandleCollision()
    {
        if (_pendingCollisionImpulse <= 0f) return;
        Engineering.ApplyDamage(_pendingCollisionImpulse, _pendingCollisionNormal,
            GlobalTransform.Basis, DamageScaleN, ZoneThreshold);
        GD.Print($"[EnemyShip {Name}] collision {_pendingCollisionImpulse:F0} N: {HealthSummary()}");
        _pendingCollisionImpulse = 0f;
        _pendingCollisionNormal = Vector3.Zero;
    }

    // Physics thread: plain fields only, no node/SimBus calls (CLAUDE.md invariant).
    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        for (int i = 0; i < state.GetContactCount(); i++)
        {
            float impulse = state.GetContactImpulse(i).Length();
            if (impulse > CollisionAlertThresholdN && impulse > _pendingCollisionImpulse)
            {
                _pendingCollisionImpulse = impulse;
                _pendingCollisionNormal = state.Transform.Basis * state.GetContactLocalNormal(i);
            }
        }
    }

    private void Destroy()
    {
        GD.Print($"[EnemyShip {Name}] {State} -> Destroyed (hull 0)");
        State = EnemyAiState.Destroyed;
        _destroyedTimer = DestroyedLingerS;
        RemoveFromGroup("lockable_targets");
        if (_turret != null && IsInstanceValid(_turret)) { _turret.SetFiring(false); _turret.Armed = false; }
    }

    private string HealthSummary() =>
        $"hull {Engineering.Hull.Health} eng {Engineering.Engines.Health} wpn {Engineering.Weapons.Health} " +
        $"rct {Engineering.Reactor.Health} state {State}";
}
