using System;
using System.Text.Json;
using Godot;
using MQTTnet.Protocol;

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
// RCS is a separate toggle from the main engine per the physical console
// design -- precision maneuvering thrusters, independent of main thrust
// and rotation (V to toggle, gates the strafe axes only).
//
// Known simplifications (fine for a first pass, worth revisiting later):
//  - Keyboard bindings are a placeholder for HOTAS + physical panel input.
public partial class PlayerShip : RigidBody3D
{
    [Export] public float ThrustForce { get; set; } = 4000f;       // Newtons, main engine
    [Export] public float RcsForce { get; set; } = 800f;            // Newtons, precision thrusters
    [Export] public float TorqueForce { get; set; } = 800f;         // Newton-metres, per axis
    [Export] public float LinearDampenerGain { get; set; } = 2.0f;  // higher = snappier auto-brake
    [Export] public float AngularDampenerGain { get; set; } = 2.0f;
    [Export] public float MixShiftDuration { get; set; } = 1.0f;    // seconds, Economy<->Power lerp
    [Export] public float HeatGenerationRate { get; set; } = 70f;   // deg/sec at full throttle+power
    [Export] public float CoolingRate { get; set; } = 0.02f;        // fraction of current temp/sec
    [Export] public float MaxEngineTemp { get; set; } = 900f;       // deg C, propulsion cutoff
    [Export] public float FtlChargeDuration { get; set; } = 5f;     // seconds, VECTOR spool-up
    [Export] public float FtlJumpDuration { get; set; } = 3f;       // seconds, JUMP execution
    [Export] public float FtlJumpDistance { get; set; } = 5000f;    // metres, placeholder per-destination offset
    [Export] public float TelemetryPublishRateHz { get; set; } = 10f; // MQTT telemetry publish rate
    [Export] public NodePath DebugLabelPath { get; set; } = new NodePath();
    [Export] public NodePath HelpLabelPath { get; set; } = new NodePath();

    private bool _helpVisible = false;
    private float _propellantMix = 0f;       // 0 = Economy, 1 = Power
    private float _engineTemp = 0f;          // deg C, 0-1000
    private bool _propulsionOverheated = false;
    private FtlPhase _ftlPhase = FtlPhase.Idle;
    private float _ftlTimer = 0f;
    private bool _ftlAborted = false;
    private float _telemetryPublishAccumulator = 0f;
    private Label _debugLabel;
    private Label _helpLabel;

    // "Last published" snapshots for the on-change state topics (see
    // PublishMqttState). Only updated when Publish() confirms the send was
    // actually attempted while connected -- see MqttTelemetryPublisher.
    private bool _mqttPropulsionStateInitialized = false;
    private float _mqttLastMix;
    private bool _mqttLastRcsEnabled;
    private bool _mqttLastDampenersEnabled;
    private bool _mqttLastOverheated;

    private bool _mqttFtlStateInitialized = false;
    private FtlPhase _mqttLastFtlPhase;
    private bool _mqttLastFtlArmed;
    private int _mqttLastFtlDestinationIndex;
    private bool _mqttLastFtlAborted;

    private const string HelpText =
        "Controls\n" +
        "--------\n" +
        "W / S        Thrust forward / reverse\n" +
        "A / D        Yaw left / right\n" +
        "Up / Down    Pitch up / down\n" +
        "Q / E        Roll left / right\n" +
        "X            Toggle dampeners\n" +
        "V            Toggle RCS\n" +
        "Z / C        Strafe left / right (RCS)\n" +
        "R / F        Strafe up / down (RCS)\n" +
        "1 / 2        Propellant mix: Economy / Power\n" +
        "?            Toggle this help";

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
        RegisterKeyAction("toggle_rcs", Key.V);
        RegisterKeyAction("strafe_left", Key.Z);
        RegisterKeyAction("strafe_right", Key.C);
        RegisterKeyAction("strafe_up", Key.R);
        RegisterKeyAction("strafe_down", Key.F);
        RegisterKeyAction("mix_economy", Key.Key1);
        RegisterKeyAction("mix_power", Key.Key2);
        RegisterKeyAction("toggle_help", Key.Slash); // "?" is Shift+/ on this key

        if (!DebugLabelPath.IsEmpty)
        {
            _debugLabel = GetNodeOrNull<Label>(DebugLabelPath);
        }

        if (!HelpLabelPath.IsEmpty)
        {
            _helpLabel = GetNodeOrNull<Label>(HelpLabelPath);
            if (_helpLabel != null)
            {
                _helpLabel.Text = HelpText;
                _helpLabel.Visible = _helpVisible;
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        HandleMix(dt);
        HandleThrust(dt);
        HandleStrafe();
        HandleRotation();
        HandleDampenerToggle();
        HandleRcsToggle();
        HandleHelpToggle();
        HandleFtl(dt);
        UpdateDebugLabel();
        PublishTelemetry();
        PublishMqttState();
        PublishMqttTelemetry(dt);
    }

    private void HandleMix(float dt)
    {
        // Mix target lives on SimBus, not a local field, so the Propulsion
        // UI panel's knob and the keyboard placeholder both drive the same
        // value -- last writer wins, which is fine since only one input
        // source is normally active at a time.
        if (Input.IsActionPressed("mix_economy")) SimBus.Instance.Propulsion.MixTarget = 0f;
        if (Input.IsActionPressed("mix_power")) SimBus.Instance.Propulsion.MixTarget = 1f;

        float mixTarget = SimBus.Instance.Propulsion.MixTarget;
        if (_propellantMix != mixTarget)
        {
            float maxStep = dt / MixShiftDuration;
            _propellantMix = Mathf.MoveToward(_propellantMix, mixTarget, maxStep);
        }
    }

    private void HandleThrust(float dt)
    {
        float thrustInput = 0f;
        if (Input.IsActionPressed("thrust_forward")) thrustInput += 1f;
        if (Input.IsActionPressed("thrust_reverse")) thrustInput -= 1f;

        if (thrustInput != 0f && !_propulsionOverheated)
        {
            float effectiveThrust = ThrustForce * (0.6f + 0.8f * _propellantMix);
            Vector3 forward = -GlobalTransform.Basis.Z; // Godot forward is -Z
            ApplyCentralForce(forward * effectiveThrust * thrustInput);

            _engineTemp += Mathf.Abs(thrustInput) * _propellantMix * HeatGenerationRate * dt;
        }
        else if (thrustInput == 0f && SimBus.Instance.Propulsion.DampenersEnabled)
        {
            // Velocity-proportional brake toward zero. Not a true PD
            // controller, just enough to feel like the ship "wants" to
            // stop drifting when you let go. Deliberately does NOT engage
            // just because overheat blocked thrust below -- dampeners are
            // a "no active input" behavior, not an overheat side effect,
            // so an overheated ship coasts on residual velocity instead of
            // snap-braking while the player is still holding thrust.
            ApplyCentralForce(-LinearVelocity * LinearDampenerGain * Mass);
        }

        // Passive radiative cooling always applies, even mid-burn, so heat
        // generation above is the net-of-cooling delta in practice.
        _engineTemp -= _engineTemp * CoolingRate * dt;
        _engineTemp = Mathf.Clamp(_engineTemp, 0f, 1000f);

        if (!_propulsionOverheated && _engineTemp >= MaxEngineTemp)
        {
            _propulsionOverheated = true;
        }
        else if (_propulsionOverheated && _engineTemp < MaxEngineTemp)
        {
            _propulsionOverheated = false;
        }
    }

    private void HandleStrafe()
    {
        if (!SimBus.Instance.Propulsion.RcsEnabled) return;

        Vector3 localStrafe = Vector3.Zero;
        if (Input.IsActionPressed("strafe_right")) localStrafe.X += 1f;
        if (Input.IsActionPressed("strafe_left")) localStrafe.X -= 1f;
        if (Input.IsActionPressed("strafe_up")) localStrafe.Y += 1f;
        if (Input.IsActionPressed("strafe_down")) localStrafe.Y -= 1f;

        if (localStrafe != Vector3.Zero)
        {
            Basis basis = GlobalTransform.Basis;
            Vector3 worldStrafe = (basis.X * localStrafe.X + basis.Y * localStrafe.Y) * RcsForce;
            ApplyCentralForce(worldStrafe);
        }
    }

    private void HandleRotation()
    {
        Vector3 localTorque = Vector3.Zero;
        bool pitchActive = false, yawActive = false, rollActive = false;

        if (Input.IsActionPressed("pitch_up")) { localTorque.X += TorqueForce; pitchActive = true; }
        if (Input.IsActionPressed("pitch_down")) { localTorque.X -= TorqueForce; pitchActive = true; }
        if (Input.IsActionPressed("yaw_left")) { localTorque.Y += TorqueForce; yawActive = true; }
        if (Input.IsActionPressed("yaw_right")) { localTorque.Y -= TorqueForce; yawActive = true; }
        if (Input.IsActionPressed("roll_left")) { localTorque.Z += TorqueForce; rollActive = true; }
        if (Input.IsActionPressed("roll_right")) { localTorque.Z -= TorqueForce; rollActive = true; }

        Basis basis = GlobalTransform.Basis;

        if (localTorque != Vector3.Zero)
        {
            // Torque input is defined relative to the ship's own axes, so
            // rotate it into world space before applying.
            ApplyTorque(basis * localTorque);
        }

        if (SimBus.Instance.Propulsion.DampenersEnabled && (!pitchActive || !yawActive || !rollActive))
        {
            // Dampen only the axes with no active input this frame, in the
            // ship's local frame, so e.g. an idle-pitching axis brakes
            // independently of an actively-rolling axis instead of the
            // whole angular-velocity vector fighting itself.
            Vector3 localAngularVelocity = basis.Transposed() * AngularVelocity;
            Vector3 localDamping = Vector3.Zero;
            if (!pitchActive) localDamping.X = -localAngularVelocity.X * AngularDampenerGain * Mass;
            if (!yawActive) localDamping.Y = -localAngularVelocity.Y * AngularDampenerGain * Mass;
            if (!rollActive) localDamping.Z = -localAngularVelocity.Z * AngularDampenerGain * Mass;

            if (localDamping != Vector3.Zero)
            {
                ApplyTorque(basis * localDamping);
            }
        }
    }

    private void HandleDampenerToggle()
    {
        if (Input.IsActionJustPressed("toggle_dampeners"))
        {
            SimBus.Instance.Propulsion.DampenersEnabled = !SimBus.Instance.Propulsion.DampenersEnabled;
        }
    }

    private void HandleRcsToggle()
    {
        if (Input.IsActionJustPressed("toggle_rcs"))
        {
            SimBus.Instance.Propulsion.RcsEnabled = !SimBus.Instance.Propulsion.RcsEnabled;
        }
    }

    private void HandleHelpToggle()
    {
        if (Input.IsActionJustPressed("toggle_help"))
        {
            _helpVisible = !_helpVisible;
            if (_helpLabel != null)
            {
                _helpLabel.Visible = _helpVisible;
            }
        }
    }

    private void UpdateDebugLabel()
    {
        if (_debugLabel == null) return;

        string mixLabel = _propellantMix <= 0f ? "Economy"
            : _propellantMix >= 1f ? "Power"
            : $"{_propellantMix * 100f:0}%";

        string tempLine = _propulsionOverheated
            ? $"Temp: {_engineTemp:0}C -- PROPULSION DISABLED (overheat)"
            : $"Temp: {_engineTemp:0}C";

        _debugLabel.Text =
            $"Velocity: {LinearVelocity.Length():0.0} m/s\n" +
            $"Dampeners: {(SimBus.Instance.Propulsion.DampenersEnabled ? "ON" : "OFF")} (X to toggle)\n" +
            $"RCS: {(SimBus.Instance.Propulsion.RcsEnabled ? "ON" : "OFF")} (V to toggle)\n" +
            $"Mix: {mixLabel} (1=Economy, 2=Power)\n" +
            tempLine + "\n" +
            "? for controls";
    }

    private void PublishTelemetry()
    {
        SimBus.Instance.Propulsion.PublishTelemetry(
            _propellantMix, _engineTemp, _propulsionOverheated, _propulsionOverheated, LinearVelocity.Length());
    }

    // MQTT publish paths alongside PublishTelemetry() above: that one
    // updates SimBus in-process every physics frame for the existing Godot
    // UI window; these push the same panel state out over MQTT for external
    // subscribers (master plan §3.4's eventual browser-based aux panels),
    // following the state-vs-telemetry topic split §3.1b establishes for
    // the hardpoints contract. Reads back from SimBus rather than local
    // fields so there's one source of truth for "what a panel's state
    // currently is" -- by this point in the frame both PublishTelemetry()
    // and HandleFtl()'s ftl.PublishTelemetry() have already run.

    // Discrete/settable state a display needs correct on reconnect:
    // retained, QoS 1, published only when something actually changed
    // (§3.1b's performance note -- controls publish on change, not
    // continuously), not throttled to TelemetryPublishRateHz since a
    // change should reach subscribers immediately, not wait for the next
    // tick window.
    private void PublishMqttState()
    {
        var mqtt = SimBus.Instance.Mqtt;
        PublishPropulsionStateIfChanged(mqtt);
        PublishFtlStateIfChanged(mqtt);
    }

    private void PublishPropulsionStateIfChanged(MqttTelemetryPublisher mqtt)
    {
        var p = SimBus.Instance.Propulsion;
        bool changed = !_mqttPropulsionStateInitialized
            || p.PropellantMix != _mqttLastMix
            || p.RcsEnabled != _mqttLastRcsEnabled
            || p.DampenersEnabled != _mqttLastDampenersEnabled
            || p.Overheated != _mqttLastOverheated;
        if (!changed) return;

        string payload = JsonSerializer.Serialize(new
        {
            mix = p.PropellantMix,
            rcs_enabled = p.RcsEnabled,
            dampeners_enabled = p.DampenersEnabled,
            overheated = p.Overheated,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        bool sent = mqtt.Publish("coldorbit/output/propulsion/state", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
        if (!sent) return; // not connected -- retry next tick against the same unsent snapshot

        _mqttLastMix = p.PropellantMix;
        _mqttLastRcsEnabled = p.RcsEnabled;
        _mqttLastDampenersEnabled = p.DampenersEnabled;
        _mqttLastOverheated = p.Overheated;
        _mqttPropulsionStateInitialized = true;
    }

    private void PublishFtlStateIfChanged(MqttTelemetryPublisher mqtt)
    {
        var ftl = SimBus.Instance.Ftl;
        bool changed = !_mqttFtlStateInitialized
            || ftl.Phase != _mqttLastFtlPhase
            || ftl.Armed != _mqttLastFtlArmed
            || ftl.DestinationIndex != _mqttLastFtlDestinationIndex
            || ftl.Aborted != _mqttLastFtlAborted;
        if (!changed) return;

        string payload = JsonSerializer.Serialize(new
        {
            phase = ftl.Phase.ToString().ToLowerInvariant(),
            armed = ftl.Armed,
            destination_index = ftl.DestinationIndex,
            aborted = ftl.Aborted,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        bool sent = mqtt.Publish("coldorbit/output/ftl/state", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
        if (!sent) return;

        _mqttLastFtlPhase = ftl.Phase;
        _mqttLastFtlArmed = ftl.Armed;
        _mqttLastFtlDestinationIndex = ftl.DestinationIndex;
        _mqttLastFtlAborted = ftl.Aborted;
        _mqttFtlStateInitialized = true;
    }

    // Live numeric readouts where staleness is worse than a brief gap:
    // non-retained, QoS 0, throttled to TelemetryPublishRateHz rather than
    // published every physics frame. FTL telemetry is deliberately not
    // published this batch -- see the batch 6 handback for why (signal-lag
    // telemetry doesn't exist yet, and charge/jump progress alone isn't
    // worth a topic that gets reshaped as soon as that lands).
    private void PublishMqttTelemetry(float dt)
    {
        _telemetryPublishAccumulator += dt;
        float interval = 1f / Mathf.Max(TelemetryPublishRateHz, 0.01f);
        if (_telemetryPublishAccumulator < interval) return;
        _telemetryPublishAccumulator = 0f;

        var propulsion = SimBus.Instance.Propulsion;
        string payload = JsonSerializer.Serialize(new
        {
            engine_temp = propulsion.EngineTemp,
            velocity = propulsion.Velocity,
        });
        SimBus.Instance.Mqtt.Publish("coldorbit/output/propulsion/telemetry", payload, MqttQualityOfServiceLevel.AtMostOnce, retain: false);
    }

    // FTL jump-drive mechanic (master plan §2; documentation/panel-control-designs.md
    // "FTL" section). Arm gates the stack. VECTOR is one action combining
    // destination lock, the jump-point clear check, and the start of the
    // spool-up charge; JUMP executes once charged. Overheat is currently the
    // only thing that can abort an in-progress charge/jump (per the
    // Propulsion section: "overheating ... is also the condition that aborts
    // an in-progress jump") -- read via the decoupled IsPropulsionDisabled
    // flag so a future damage/sabotage system can trigger the same abort
    // without this method changing.
    private void HandleFtl(float dt)
    {
        var ftl = SimBus.Instance.Ftl;
        bool inFlight = _ftlPhase == FtlPhase.Charging || _ftlPhase == FtlPhase.Jumping;

        if (inFlight && SimBus.Instance.Propulsion.IsPropulsionDisabled)
        {
            _ftlPhase = FtlPhase.Idle;
            _ftlTimer = 0f;
            _ftlAborted = true;
            ftl.Armed = false; // force re-arm before another attempt
        }
        else if (!ftl.Armed && _ftlPhase != FtlPhase.Idle)
        {
            // Deliberate disarm cancels whatever stage FTL was in -- Arm
            // gates the whole stack (panel-control-designs.md convention).
            // Not an abort: no fault occurred, so _ftlAborted stays as-is.
            _ftlPhase = FtlPhase.Idle;
            _ftlTimer = 0f;
        }

        switch (_ftlPhase)
        {
            case FtlPhase.Idle:
                if (ftl.Armed && ftl.VectorRequested)
                {
                    // No obstruction/traffic model exists yet, so the
                    // jump-point clear check always passes -- see
                    // panel-control-designs.md "Open dependency".
                    _ftlPhase = FtlPhase.Charging;
                    _ftlTimer = 0f;
                    _ftlAborted = false;
                }
                break;

            case FtlPhase.Charging:
                _ftlTimer += dt;
                if (_ftlTimer >= FtlChargeDuration)
                {
                    _ftlPhase = FtlPhase.Ready;
                    _ftlTimer = 0f;
                }
                break;

            case FtlPhase.Ready:
                if (ftl.JumpRequested)
                {
                    _ftlPhase = FtlPhase.Jumping;
                    _ftlTimer = 0f;
                }
                break;

            case FtlPhase.Jumping:
                _ftlTimer += dt;
                if (_ftlTimer >= FtlJumpDuration)
                {
                    ExecuteJump(ftl.DestinationIndex);
                    _ftlPhase = FtlPhase.Complete;
                    _ftlTimer = 0f;
                }
                break;

            case FtlPhase.Complete:
                // Holds solid-green until the player disarms (handled by the
                // arm-gate branch above), matching the panel's "solid when
                // complete" LED behaviour.
                break;
        }

        ftl.VectorRequested = false;
        ftl.JumpRequested = false;

        bool chargedOrLater = _ftlPhase is FtlPhase.Ready or FtlPhase.Jumping or FtlPhase.Complete;
        float chargeProgress = _ftlPhase == FtlPhase.Charging ? _ftlTimer / FtlChargeDuration : (chargedOrLater ? 1f : 0f);
        float jumpProgress = _ftlPhase == FtlPhase.Jumping ? _ftlTimer / FtlJumpDuration : (_ftlPhase == FtlPhase.Complete ? 1f : 0f);

        ftl.PublishTelemetry(_ftlPhase, chargeProgress, jumpProgress, _ftlAborted);
    }

    private void ExecuteJump(int destinationIndex)
    {
        // Placeholder translation: no real star-system positions exist yet
        // (README: "empty space ... no FTL yet"), so each destination maps
        // to a fixed offset along the ship's current heading rather than an
        // actual coordinate. Velocity resets to zero, since a jump is a
        // discrete relocation, not a continuous burn.
        Vector3 forward = -GlobalTransform.Basis.Z;
        GlobalPosition += forward * FtlJumpDistance * (destinationIndex + 1);
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
    }

    private void RegisterKeyAction(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
    }
}
