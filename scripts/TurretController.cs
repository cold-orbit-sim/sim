using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ColdOrbit.SimCore;

public enum TurretLockState { None, Acquiring, Locked }

// Physically rotates a turret (yaw + pitch) to track a selected target and runs
// the two-phase lock sequence (aim, then a range-scaled "computing trajectory"
// timer). No firing logic — see batch 24 handover.
//
// This node IS the yaw pivot: it's created at runtime as a new child of the
// turret root (see ShipMesh.AttachTurretController) rather than a script attached
// to the GLB-imported root node itself. The GLB root's ring mesh and the mantle/
// barrels are reparented under this node (ring directly, mantle+barrels via a
// child PitchPivot) so rotating this node yaws everything together, and rotating
// PitchPivot pitches the mantle+barrels without affecting the ring. Deviation
// from the handover's literal "turret root becomes the yaw pivot" — attaching a
// C# script to an already-instanced GLB node at runtime is awkward to do safely
// in C# (no clean cast back to the new type), so a fresh pivot node sidesteps it
// with the same net effect.
public partial class TurretController : Node3D
{
    [Export] public string TurretId { get; set; } = "dorsal"; // "dorsal" or "ventral" — matches MQTT topic segment

    [Export] public float YawTurnRateDegPerSec { get; set; } = 90f;
    [Export] public float PitchTurnRateDegPerSec { get; set; } = 90f;
    [Export] public float AimToleranceDeg { get; set; } = 3f;

    [Export] public float MinPitchDeg { get; set; } = -180f; // placeholder — no real hull-collision limit yet
    [Export] public float MaxPitchDeg { get; set; } = 180f;

    [Export] public float MinLockTimeS { get; set; } = 0.5f;  // fastest possible lock, at close range
    [Export] public float MaxLockTimeS { get; set; } = 5.0f;  // slowest, at max test range
    [Export] public float RangeLockDivisor { get; set; } = 160f; // lock_time = clamp(range_m / this, Min, Max)

    // Written by ShipMesh each frame from SimBus.Turret{Dorsal,Ventral}.Armed
    // (the control panel is the writer). An unarmed turret stops tracking and
    // drops its target — see _Process.
    public bool Armed { get; set; } = false;

    private Node3D _pitchPivot;
    private Node3D _restFrame;   // turret root: never rotates, the reference frame for aim angles
    private Node3D _hullFrame;   // ShipMesh: the frame reported bearing/elevation are measured in
    private Node3D _selectedTarget;
    private TurretLockState _lockState = TurretLockState.None;
    private float _lockTimer = 0f;
    private float _lockTimeRequired = 0f;

    private string _prevAction;
    private string _nextAction;

    // Setup runs immediately after AddChild rather than from _Ready, so the pitch
    // pivot exists before the first _Process call regardless of node-entering-tree
    // timing — see the class doc comment for why this node exists at all.
    public void Setup(Node3D hullFrame, Node3D ring, Node3D mantle, Node3D barrelA, Node3D barrelB)
    {
        _restFrame = GetParent<Node3D>();
        _hullFrame = hullFrame;

        ring.Reparent(this, true);

        _pitchPivot = new Node3D { Name = "PitchPivot" };
        AddChild(_pitchPivot);
        // Position at the mantle (so pitch rotates about the trunnion, not some
        // arbitrary point) but take ORIENTATION from this yaw pivot, not from the
        // mantle. The mantle/barrel meshes carry baked authoring rotations (the
        // barrels are Rz(90) — cylinders modelled along +Y then laid along +X);
        // inheriting those would make _pitchPivot.RotationDegrees mean something
        // other than elevation and break both the aim math and the telemetry.
        _pitchPivot.GlobalTransform = new Transform3D(GlobalTransform.Basis, mantle.GlobalPosition);

        mantle.Reparent(_pitchPivot, true);
        barrelA.Reparent(_pitchPivot, true);
        barrelB.Reparent(_pitchPivot, true);

        RegisterKeyActions();
    }

    private void RegisterKeyActions()
    {
        // Placeholder input — no physical turret panel exists yet. Same pattern as
        // the F1-F7 camera shortcuts (batch 18): bypasses MQTT entirely, bound
        // directly to SelectTarget/cycling. A real turret target-select MQTT input
        // contract is still needed before hardware arrives.
        Key prevKey, nextKey;
        switch (TurretId)
        {
            case "dorsal":
                prevKey = Key.Bracketleft;
                nextKey = Key.Bracketright;
                break;
            case "ventral":
                prevKey = Key.Comma;
                nextKey = Key.Period;
                break;
            default:
                GD.PrintErr($"TurretController: unknown TurretId '{TurretId}' — no keyboard shortcut bound");
                return;
        }

        _prevAction = $"turret_{TurretId}_prev";
        _nextAction = $"turret_{TurretId}_next";
        RegisterKeyAction(_prevAction, prevKey);
        RegisterKeyAction(_nextAction, nextKey);
    }

    private static void RegisterKeyAction(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
    }

    public void SelectTarget(Node3D target)
    {
        _selectedTarget = target;
        _lockState = TurretLockState.None;
        _lockTimer = 0f;
    }

    public void ClearTarget()
    {
        _selectedTarget = null;
        _lockState = TurretLockState.None;
        _lockTimer = 0f;
    }

    // Cycles to the next/previous member of the "lockable_targets" group, sorted
    // by name for a stable, deterministic order across frames.
    public void CycleTarget(int direction)
    {
        var targets = GetTree().GetNodesInGroup("lockable_targets")
            .OfType<Node3D>()
            .OrderBy(n => n.Name.ToString())
            .ToList();
        if (targets.Count == 0) return;

        int currentIndex = _selectedTarget != null ? targets.IndexOf(_selectedTarget) : -1;
        int nextIndex = currentIndex < 0
            ? (direction > 0 ? 0 : targets.Count - 1)
            : (currentIndex + direction + targets.Count) % targets.Count;

        SelectTarget(targets[nextIndex]);
    }

    public override void _Process(double delta)
    {
        if (_prevAction != null && Input.IsActionJustPressed(_prevAction)) CycleTarget(-1);
        if (_nextAction != null && Input.IsActionJustPressed(_nextAction)) CycleTarget(1);

        // An unarmed turret is inert: it drops the target and stops tracking.
        // Cycling above still works so the panel can pre-select before arming,
        // but ClearTarget here means selecting while unarmed won't hold.
        if (!Armed)
        {
            if (_selectedTarget != null) ClearTarget();
            return;
        }

        if (_selectedTarget == null || !IsInstanceValid(_selectedTarget))
        {
            if (_selectedTarget != null) ClearTarget(); // target was freed/destroyed
            return;
        }

        float dt = (float)delta;
        Vector3 toTarget = _selectedTarget.GlobalPosition - GlobalPosition;
        float range = toTarget.Length();

        // Aim angles are computed in the REST frame (the turret root, which never
        // rotates), not in this node's own frame. Using GlobalTransform here would
        // measure the target's bearing relative to where the turret is already
        // pointing, then apply that as an absolute rotation — a feedback loop that
        // makes the turret rotate forever and never converge.
        Vector3 localToTarget = _restFrame.GlobalTransform.Basis.Inverse() * toTarget;

        // Rig convention (confirmed from cruiser.glb): the turret root has no baked
        // rotation, so its local axes are the model's — forward is +X (barrels sit
        // at x=4.6, ahead of the mantle at x=1.0), up is +Y. So yaw turns about Y
        // with rest heading +X, and pitch turns about Z (NOT X — Rz swings +X
        // toward +Y, i.e. elevation; Rx would swing the barrels sideways).
        float targetYawDeg = Mathf.RadToDeg(Mathf.Atan2(-localToTarget.Z, localToTarget.X));
        RotateTurretYaw(targetYawDeg, dt);

        float horizontalDist = new Vector2(localToTarget.X, localToTarget.Z).Length();
        float targetPitchDeg = Mathf.RadToDeg(Mathf.Atan2(localToTarget.Y, horizontalDist));
        targetPitchDeg = Mathf.Clamp(targetPitchDeg, MinPitchDeg, MaxPitchDeg);
        RotateTurretPitch(targetPitchDeg, dt);

        bool aimed = IsAimed(targetYawDeg, targetPitchDeg);
        UpdateLockState(aimed, range, dt);
    }

    private void RotateTurretYaw(float targetYawDeg, float dt)
    {
        float currentYaw = RotationDegrees.Y;
        float newYaw = Mathf.RotateToward(
            Mathf.DegToRad(currentYaw), Mathf.DegToRad(targetYawDeg), Mathf.DegToRad(YawTurnRateDegPerSec * dt));
        RotationDegrees = new Vector3(RotationDegrees.X, Mathf.RadToDeg(newYaw), RotationDegrees.Z);
    }

    private void RotateTurretPitch(float targetPitchDeg, float dt)
    {
        float currentPitch = _pitchPivot.RotationDegrees.Z;
        float newPitch = Mathf.RotateToward(
            Mathf.DegToRad(currentPitch), Mathf.DegToRad(targetPitchDeg), Mathf.DegToRad(PitchTurnRateDegPerSec * dt));
        _pitchPivot.RotationDegrees = new Vector3(_pitchPivot.RotationDegrees.X, _pitchPivot.RotationDegrees.Y, Mathf.RadToDeg(newPitch));
    }

    private bool IsAimed(float targetYawDeg, float targetPitchDeg)
    {
        float yawError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(
            Mathf.DegToRad(RotationDegrees.Y), Mathf.DegToRad(targetYawDeg))));
        float pitchError = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(
            Mathf.DegToRad(_pitchPivot.RotationDegrees.Z), Mathf.DegToRad(targetPitchDeg))));
        return yawError <= AimToleranceDeg && pitchError <= AimToleranceDeg;
    }

    private void UpdateLockState(bool aimed, float range, float dt)
    {
        if (!aimed)
        {
            // Pause, don't reset — per spec, brief tracking loss on a moving target
            // shouldn't throw away lock progress.
            if (_lockState == TurretLockState.Acquiring) return;
            _lockState = TurretLockState.None;
            return;
        }

        if (_lockState == TurretLockState.Locked) return; // already locked, nothing to do until target changes

        if (_lockState == TurretLockState.None)
        {
            _lockState = TurretLockState.Acquiring;
            _lockTimeRequired = Mathf.Clamp(range / RangeLockDivisor, MinLockTimeS, MaxLockTimeS);
            _lockTimer = 0f;
        }

        _lockTimer += dt;
        if (_lockTimer >= _lockTimeRequired)
            _lockState = TurretLockState.Locked;
    }

    public TurretLockState LockState => _lockState;

    // Lock acquisition progress, 0–1. Follows the FTL charge-cycle convention
    // (SimBus.FtlState.Progress): a plain float that's always meaningful rather
    // than a nullable — 0 with no target, ramping while acquiring, 1 once locked.
    // Holds its value (doesn't reset) while aim is lost mid-acquisition, matching
    // the pause-don't-reset rule in UpdateLockState.
    public float LockProgress => _lockState switch
    {
        TurretLockState.Locked    => 1f,
        TurretLockState.Acquiring => Mathf.Clamp(_lockTimer / Mathf.Max(_lockTimeRequired, 0.0001f), 0f, 1f),
        _                         => 0f,
    };

    // Reported bearing/elevation are measured from the BARREL'S ACTUAL DIRECTION
    // expressed in hull space — deliberately not from the pivots' Euler angles.
    //
    // The two turret roots don't share a frame: turret_ventral_fwd is baked with a
    // 180° roll about X, so its local +Y points down and its local +Z points to
    // port. Reading RotationDegrees off each pivot would therefore report the two
    // turrets' pan (and elevation) with opposite signs, and a per-turret sign flip
    // would be one more thing to get wrong the next time a turret is added with a
    // different baked orientation. Deriving from the real direction vector is
    // orientation-agnostic and self-correcting.
    //
    // Hull space is ShipMesh's own local space, i.e. the GLB model frame:
    // +X = nose, +Y = up, +Z = starboard. So bearing is a true bearing from the
    // front of the ship — 0° dead ahead, 90° starboard, 180° astern, 270° port —
    // and elevation is 0° level, positive up, matching the §3.1b contract.
    //
    // NOTE: this is display/telemetry only. The traverse control loop in _Process
    // stays in rig space and is unaffected.
    private Vector3 BarrelDirInHullSpace()
    {
        if (_pitchPivot == null || _hullFrame == null) return Vector3.Zero;
        Vector3 worldDir = _pitchPivot.GlobalTransform.Basis.X.Normalized();
        return (_hullFrame.GlobalTransform.Basis.Orthonormalized().Inverse() * worldDir).Normalized();
    }

    public float CurrentBearingDeg
    {
        get
        {
            Vector3 d = BarrelDirInHullSpace();
            return Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(d.Z, d.X)), 360f);
        }
    }

    public float CurrentElevationDeg
    {
        get
        {
            Vector3 d = BarrelDirInHullSpace();
            return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f)));
        }
    }
    public float CurrentRangeM => _selectedTarget != null
        ? GlobalPosition.DistanceTo(_selectedTarget.GlobalPosition)
        : 0f;
    // Target.cs carries TargetDisplayName/TargetClass/TargetAlliance specifically
    // shaped to match the turret MQTT contract's target_* fields, so they pass
    // straight through with no translation.
    public string TargetName => _selectedTarget is Target t ? t.TargetDisplayName : null;
    public string TargetClass => _selectedTarget is Target t ? t.TargetClass : null;
    public string TargetAlliance => _selectedTarget is Target t ? t.TargetAlliance : null;
    public bool HasTarget => _selectedTarget != null;
}
