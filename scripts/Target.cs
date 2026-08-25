using Godot;

namespace ColdOrbit.SimCore;

public partial class Target : Node3D
{
    // Metadata shaped to match the existing turret MQTT contract fields
    // (target_name / target_class / target_alliance) so a future lock-on
    // system can read these directly with no redesign.
    [Export] public string TargetDisplayName { get; set; } = "Target Drone";
    [Export] public string TargetClass { get; set; } = "Debug Target";
    [Export] public string TargetAlliance { get; set; } = "Neutral";
    [Export] public bool IsMoving { get; set; } = false;
    [Export] public Vector3 MoveAxis { get; set; } = Vector3.Right; // local axis to oscillate along
    [Export] public float MoveAmplitudeM { get; set; } = 150f;      // distance either side of spawn point
    [Export] public float MoveSpeed { get; set; } = 0.3f;           // higher = faster oscillation

    private Vector3 _basePosition;
    private double _elapsed = 0.0;

    public override void _Ready()
    {
        _basePosition = Position;
        AddToGroup("lockable_targets"); // future TargetingSystem/turret lock code queries this group
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsMoving) return;

        _elapsed += delta;
        float offset = Mathf.Sin((float)_elapsed * MoveSpeed) * MoveAmplitudeM;
        Position = _basePosition + MoveAxis.Normalized() * offset;
    }
}
