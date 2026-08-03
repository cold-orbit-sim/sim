using Godot;

namespace ColdOrbit.SimCore;

// First-pass flight model for the sim-core prototype.
//
// Physics: Newtonian (thrust/torque obey F=ma with no artificial speed cap)
// with inertia dampeners that actively counter drift/spin ONLY on axes with
// no active input. Dampeners default on; toggle with X for now.
//
// This stands in for the Propulsion panel's RCS/dampener control (master
// plan §7.7) until that's wired up over MQTT. Input is registered in code
// rather than project.godot's InputMap, so this scene needs zero editor
// setup -- open the project and press Play.
//
// Known simplifications (fine for a first pass, worth revisiting later):
//  - Angular dampening brakes the whole angular-velocity vector at once,
//    not per-axis -- rolling while idle-pitching will fight itself a bit.
//  - No RCS/strafe translation yet -- only main-engine forward/reverse
//    thrust and rotation. Add a translate axis once RCS is modeled.
//  - Keyboard bindings are a placeholder for HOTAS + physical panel input.
public partial class PlayerShip : RigidBody3D
{
    [Export] public float ThrustForce { get; set; } = 4000f;       // Newtons, main engine
    [Export] public float TorqueForce { get; set; } = 800f;         // Newton-metres, per axis
    [Export] public float LinearDampenerGain { get; set; } = 2.0f;  // higher = snappier auto-brake
    [Export] public float AngularDampenerGain { get; set; } = 2.0f;
    [Export] public NodePath DebugLabelPath { get; set; } = new NodePath();

    private bool _dampenersEnabled = true;
    private Label _debugLabel;

    public override void _Ready()
    {
        GravityScale = 0f; // no gravity in space

        RegisterKeyAction("thrust_forward", Key.W);
        RegisterKeyAction("thrust_reverse", Key.S);
        RegisterKeyAction("yaw_left", Key.A);
        RegisterKeyAction("yaw_right", Key.D);
        RegisterKeyAction("pitch_up", Key.Up);
        RegisterKeyAction("pitch_down", Key.Down);
        RegisterKeyAction("roll_left", Key.Q);
        RegisterKeyAction("roll_right", Key.E);
        RegisterKeyAction("toggle_dampeners", Key.X);

        if (!DebugLabelPath.IsEmpty())
        {
            _debugLabel = GetNodeOrNull<Label>(DebugLabelPath);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        HandleThrust();
        HandleRotation();
        HandleDampenerToggle();
        UpdateDebugLabel();
    }

    private void HandleThrust()
    {
        float thrustInput = 0f;
        if (Input.IsActionPressed("thrust_forward")) thrustInput += 1f;
        if (Input.IsActionPressed("thrust_reverse")) thrustInput -= 1f;

        if (thrustInput != 0f)
        {
            Vector3 forward = -GlobalTransform.Basis.Z; // Godot forward is -Z
            ApplyCentralForce(forward * ThrustForce * thrustInput);
        }
        else if (_dampenersEnabled)
        {
            // Velocity-proportional brake toward zero. Not a true PD
            // controller, just enough to feel like the ship "wants" to
            // stop drifting when you let go.
            ApplyCentralForce(-LinearVelocity * LinearDampenerGain * Mass);
        }
    }

    private void HandleRotation()
    {
        Vector3 localTorque = Vector3.Zero;
        if (Input.IsActionPressed("pitch_up")) localTorque.X += TorqueForce;
        if (Input.IsActionPressed("pitch_down")) localTorque.X -= TorqueForce;
        if (Input.IsActionPressed("yaw_left")) localTorque.Y += TorqueForce;
        if (Input.IsActionPressed("yaw_right")) localTorque.Y -= TorqueForce;
        if (Input.IsActionPressed("roll_left")) localTorque.Z += TorqueForce;
        if (Input.IsActionPressed("roll_right")) localTorque.Z -= TorqueForce;

        if (localTorque != Vector3.Zero)
        {
            // Torque input is defined relative to the ship's own axes, so
            // rotate it into world space before applying.
            ApplyTorque(GlobalTransform.Basis * localTorque);
        }
        else if (_dampenersEnabled)
        {
            ApplyTorque(-AngularVelocity * AngularDampenerGain * Mass);
        }
    }

    private void HandleDampenerToggle()
    {
        if (Input.IsActionJustPressed("toggle_dampeners"))
        {
            _dampenersEnabled = !_dampenersEnabled;
        }
    }

    private void UpdateDebugLabel()
    {
        if (_debugLabel == null) return;
        _debugLabel.Text =
            $"Velocity: {LinearVelocity.Length():0.0} m/s\n" +
            $"Dampeners: {(_dampenersEnabled ? "ON" : "OFF")} (X to toggle)";
    }

    private void RegisterKeyAction(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
    }
}
