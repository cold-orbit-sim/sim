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
    [Export] public float HeatGenerationRate { get; set; } = 35f;   // deg/sec at full throttle+power
    [Export] public float CoolingRate { get; set; } = 0.02f;        // fraction of current temp/sec
    [Export] public float MaxEngineTemp { get; set; } = 900f;       // deg C, propulsion cutoff
    [Export] public float PowerPerEnginekW { get; set; } = 1500f;   // kW per engine at full throttle
    [Export] public float FtlChargeDuration { get; set; } = 5f;     // seconds, VECTOR spool-up
    [Export] public float FtlJumpDuration { get; set; } = 3f;       // seconds, JUMP execution
    [Export] public float FtlCooldownDuration { get; set; } = 5f;   // seconds, post-jump/abort cooldown
    [Export] public float FtlJumpDistance { get; set; } = 5000f;    // metres, placeholder per-destination offset
    [Export] public float MaxSignalLagS { get; set; } = 4.0f;       // seconds, FTL signal-lag peak
    [Export] public float TelemetryPublishRateHz { get; set; } = 10f; // MQTT telemetry publish rate
    [Export] public float CollisionBounce { get; set; } = 0.3f;     // PhysicsMaterial.bounce (partial restitution; tunable)
    [Export] public float CollisionFriction { get; set; } = 0.0f;   // PhysicsMaterial.friction (zero = no surface drag in space)
    [Export] public float CollisionAlertThresholdN { get; set; } = 5000f; // impulse above this raises HULL IMPACT alert
    [Export] public float CollisionAlertDurationS { get; set; } = 3f;    // how long the alert stays active after impact
    [Export] public string Callsign { get; set; } = "Cold Orbit";        // ship callsign published to coldorbit/output/ship/callsign
    [Export] public float DamageScaleN { get; set; } = 50000f;           // 50 kN impulse = 1 HP of damage
    [Export] public float ZoneThreshold { get; set; } = 0.5f;            // dot-product threshold for front/rear vs side hit zones
    [Export] public float ReactorOutputKW { get; set; } = 3000f;         // nominal reactor output for repair headroom calculation
    [Export] public float RepairKWPerHPPerSecond { get; set; } = 270f;   // kW of headroom needed per HP/s of repair rate
    [Export] public float AtmosphereTopM { get; set; } = 2000f;          // altitude (Godot units) above which density is forced to zero
    [Export] public float ScaleHeightM { get; set; } = 700f;             // atmospheric scale height (Godot units); density = exp(-alt/scale)
    [Export] public float DragCoefficient { get; set; } = 0.0002f;       // drag = v² × coeff × density (N)
    [Export] public float AtmoHeatRate { get; set; } = 15f;              // °C/s per (density × m/s)
    [Export] public NodePath DebugLabelPath { get; set; } = new NodePath();
    [Export] public NodePath HelpLabelPath { get; set; } = new NodePath();
    [Export] public NodePath PlanetPath { get; set; } = new NodePath();

    private bool _helpVisible = false;
    private float _propellantMix = 0f;       // 0 = Economy, 1 = Power
    private float _engineTemp = 0f;          // deg C, 0-1000
    private bool _propulsionOverheated = false;
    private float _thrustInput = 0f;         // 0-1, abs of current thrust axis
    private bool _reverseEnabled = false;    // true while reverse thrust key is held
    private float _previousVelocity = 0f;    // for acceleration derivation
    private float _smoothedAcceleration = 0f;
    private FtlPhase _ftlPhase = FtlPhase.Idle;
    private float _ftlTimer = 0f;
    private bool _ftlAborted = false;
    private float _ftlSignalLagS = 0f;
    private float _telemetryPublishAccumulator = 0f;
    private Label _debugLabel;
    private Label _helpLabel;

    // Cached planet node (batch 14). Resolved in _Ready primarily from
    // SimBus.Instance.Planet (set by Planet._Ready); read on the physics step
    // (_IntegrateForces, HandleThrust) and from _PhysicsProcess.
    // GlobalPosition/SoiName/GM are all reads -- safe while Planet remains a
    // non-moving StaticBody3D (see Planet.cs threading note).
    private Planet _planet;

    // Altitude above the planet surface in metres. Negative below the surface.
    private float _altitudeM = 0f;

    // Dampener mode derived each physics frame: "off", "station_keep", "orbit_hold".
    // Written in HandleThrust, published to SimBus and MQTT each telemetry tick.
    private string _dampenerMode = "off";

    // Alert state tracking -- raise/clear only on transitions so we don't
    // republish the full alerts array on every physics frame.
    private bool _alertOverheatActive = false;
    private bool _alertFtlAbortActive = false;
    private bool _alertCollisionActive = false;
    private bool _alertAtmoActive = false;
    private bool _mqttAlertsNeedPublish = true; // force publish on first connect

    // True when the ship is below AtmosphereTopM. Set each _PhysicsProcess frame
    // so HandleThrust and UpdateAlerts see a consistent value.
    private bool _inAtmosphere = false;

    // Atmospheric density at the ship's current altitude, set in _IntegrateForces
    // and read in HandleThrust (_PhysicsProcess) for heating. Volatile for
    // future-proofing against multithreaded physics.
    private volatile float _pendingAtmoDensity = 0f;

    // Collision impulse written from _IntegrateForces (physics step), read and
    // cleared in HandleCollision (_PhysicsProcess). Both run on the main thread
    // in Godot's default non-multithreaded physics mode, so no lock is needed.
    private float _pendingCollisionImpulse = 0f;
    private Vector3 _pendingCollisionNormal = Vector3.Zero; // world-space, paired with impulse
    private bool _collisionWantAlert = false;
    private float _collisionAlertTimer = 0f;

    // Hull-damage alert (distinct from alert_collision — fires when damage HP is applied).
    private bool _alertHullDamageActive = false;
    private bool _hullDamageWantAlert = false;
    private float _hullDamageAlertTimer = 0f;

    // Set of system IDs currently showing a "DISABLED" alert.
    private readonly System.Collections.Generic.HashSet<string> _alertDisabledSystems = new();

    // Mission-elapsed timer (seconds) used for alert timestamps.
    private float _missionTimeS = 0f;

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
        // No built-in gravity: batch 14 applies inverse-square gravity manually
        // in _IntegrateForces (GravityScale stays 0 via the scene node value).
        // Built-in gravity would otherwise stack 9.8 m/s² "down" on top of the
        // manual model.

        // Enable contact reporting so _IntegrateForces receives impulse data.
        ContactMonitor = true;
        MaxContactsReported = 4;

        // Build PhysicsMaterial from exported values so bounce/friction are tunable
        // in the inspector without a rebuild.
        PhysicsMaterialOverride = new PhysicsMaterial
        {
            Bounce = CollisionBounce,
            Friction = CollisionFriction,
        };

        // Planet reference comes from SimBus, set by SoiKael.OnPlayerEntered after
        // SceneManager loads the SoI. Null at _Ready time (SoI loads deferred);
        // refreshed from SimBus at the top of each _PhysicsProcess.

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

        SimBus.Instance.PlayerShipNode = this;

        // Publish ship-config values to SimBus so admin methods can use them
        // without holding a reference to PlayerShip.
        SimBus.Instance.Propulsion.PowerPerEnginekW = PowerPerEnginekW;
        SimBus.Instance.Propulsion.CollisionAlertThresholdN = CollisionAlertThresholdN;
        SimBus.Instance.ShipCallsign = Callsign;
        SimBus.Instance.Engineering.MaxEngineTempC = MaxEngineTemp;

        // Force alert re-publish after each broker reconnect so the touchscreen
        // recovers the correct alert state without waiting for a state change.
        SimBus.Instance.Mqtt.Connected += () => { _mqttAlertsNeedPublish = true; };
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _missionTimeS += dt;

        // Refresh body refs from SimBus so SoI scene swaps take effect immediately.
        _planet = SimBus.Instance.Planet;
        var _star = SimBus.Instance.StarNode;

        // Altitude above nearest body's surface. Negative = inside the body.
        if (_planet != null)
            _altitudeM = (_planet.GlobalPosition - GlobalPosition).Length() - _planet.PlanetRadius;
        else if (_star != null)
            _altitudeM = GlobalPosition.DistanceTo(_star.GlobalPosition) - _star.StarRadiusM;
        else
            _altitudeM = 0f;

        _inAtmosphere = (_planet != null && _altitudeM < AtmosphereTopM)
                     || (_star != null && _altitudeM < _star.HeatZoneAltitudeM)
                     || SimBus.Instance.Propulsion.AdminAtmoSimulated;

        HandleSpawnReset();
        HandleMix(dt);
        HandleThrust(dt);
        HandleStrafe();
        HandleRotation();
        HandleDampenerToggle();
        HandleRcsToggle();
        HandleHelpToggle();
        HandleFtl(dt);
        HandleCollision(dt);
        UpdateDebugLabel();
        PublishTelemetry(dt);
        PublishMqttState();          // immediate: alerts only
        PublishMqttTelemetry(dt);   // rate-limited: propulsion + FTL state
    }

    private void HandleSpawnReset()
    {
        if (!SimBus.Instance.Propulsion.PendingSpawnReset) return;
        SimBus.Instance.Propulsion.PendingSpawnReset = false;
        GlobalPosition  = Vector3.Zero;
        LinearVelocity  = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        Basis           = Basis.Identity;
    }

    // Reads impulse data from _IntegrateForces (physics thread → main thread handoff)
    // and from the admin simulate path. Applies damage, manages alert windows.
    private void HandleCollision(float dt)
    {
        // Pick up admin-simulated collision (set by AdminTriggerCollisionAlert).
        float adminN = SimBus.Instance.Propulsion.PendingAdminCollisionN;
        if (adminN > 0f)
        {
            if (adminN > _pendingCollisionImpulse)
            {
                _pendingCollisionImpulse = adminN;
                _pendingCollisionNormal = -GlobalTransform.Basis.Z; // treat as bow-on hit
            }
            SimBus.Instance.Propulsion.PendingAdminCollisionN = 0f;
        }

        if (_pendingCollisionImpulse > 0f)
        {
            SimBus.Instance.Propulsion.CollisionForceN = _pendingCollisionImpulse;
            _collisionAlertTimer = CollisionAlertDurationS;
            _collisionWantAlert = true;

            // Apply damage distribution and trigger hull-damage alert.
            SimBus.Instance.Engineering.ApplyDamage(
                _pendingCollisionImpulse, _pendingCollisionNormal,
                GlobalTransform.Basis, DamageScaleN, ZoneThreshold);
            _hullDamageWantAlert = true;
            _hullDamageAlertTimer = 5f;

            _pendingCollisionImpulse = 0f;
            _pendingCollisionNormal = Vector3.Zero;

            // Immediate publish so displays see damage without waiting for telemetry tick.
            SimBus.Instance.PublishEngineeringState();
            SimBus.Instance.PublishRepairQueue();
        }

        if (_collisionWantAlert)
        {
            _collisionAlertTimer -= dt;
            if (_collisionAlertTimer <= 0f)
            {
                _collisionWantAlert = false;
                SimBus.Instance.Propulsion.CollisionForceN = 0f;
            }
        }

        if (_hullDamageWantAlert)
        {
            _hullDamageAlertTimer -= dt;
            if (_hullDamageAlertTimer <= 0f)
                _hullDamageWantAlert = false;
        }
    }

    // Receives per-contact impulse data during the physics step. Writes only to
    // _pendingCollisionImpulse (a plain float) — no SimBus or node method calls,
    // as required for physics-thread callbacks.
    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        for (int i = 0; i < state.GetContactCount(); i++)
        {
            float impulse = state.GetContactImpulse(i).Length();
            if (impulse > CollisionAlertThresholdN && impulse > _pendingCollisionImpulse)
            {
                _pendingCollisionImpulse = impulse;
                // Convert local-body contact normal to world space for damage distribution.
                _pendingCollisionNormal = state.Transform.Basis * state.GetContactLocalNormal(i);
            }
        }

        // Gravity — inverse square toward planet centre (batch 14).
        // _planet.GM and _planet.GlobalPosition are reads; safe while Planet is
        // a StaticBody3D that never moves. If the planet ever becomes dynamic
        // (moving/rotating) or multithreaded physics is enabled, this read path
        // needs revisiting (see Planet.cs threading note).
        if (_planet != null)
        {
            Vector3 toCenter = _planet.GlobalPosition - state.Transform.Origin;
            float distSq = toCenter.LengthSquared();
            if (distSq > 0.01f) // guard against division by zero at planet centre
            {
                float accel = _planet.GM / distSq;
                state.ApplyCentralForce(toCenter.Normalized() * accel * Mass);
            }

            // Atmospheric drag. Zero above AtmosphereTopM; exponential below it.
            float dist = toCenter.Length();
            float altM = dist - _planet.PlanetRadius;
            float density = altM >= AtmosphereTopM ? 0f
                : Mathf.Exp(-Mathf.Max(0f, altM) / ScaleHeightM);
            _pendingAtmoDensity = density;

            Vector3 vel = state.LinearVelocity;
            float speedSq = vel.LengthSquared();
            if (speedSq > 0.01f && density > 1e-6f)
            {
                Vector3 drag = -vel.Normalized() * speedSq * DragCoefficient * density;
                state.ApplyCentralForce(drag);
            }
        }
        else
        {
            _pendingAtmoDensity = 0f;
        }
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
        _thrustInput = 0f;
        _reverseEnabled = false;
        _dampenerMode = "off"; // overwritten below if dampeners engage

        if (Input.IsActionPressed("thrust_forward")) _thrustInput += 1f;
        if (Input.IsActionPressed("thrust_reverse")) { _thrustInput += 1f; _reverseEnabled = true; }

        var prop = SimBus.Instance.Propulsion;

        // Dampeners cannot operate inside the atmosphere.
        if (_inAtmosphere && prop.DampenersEnabled)
            prop.DampenersEnabled = false;

        var eng = SimBus.Instance.Engineering;
        bool overtempBlocked = _propulsionOverheated && !prop.AdminOvertempBypass;
        if (_thrustInput > 0f && !overtempBlocked && !eng.Engines.Disabled)
        {
            float effectiveThrust = ThrustForce * prop.AdminThrustMultiplier
                * (0.6f + 0.8f * _propellantMix) * eng.ThrustMultiplier;
            float direction = _reverseEnabled ? -1f : 1f;
            Vector3 forward = -GlobalTransform.Basis.Z; // Godot forward is -Z
            ApplyCentralForce(forward * effectiveThrust * direction);

            _engineTemp += _thrustInput * _propellantMix * HeatGenerationRate * dt;
        }
        // Gravity counter: always active when dampeners are on near a planet,
        // even while thrusting — prevents the ship from falling during maneuvers.
        // Velocity cancellation (station-keep / orbit-hold) only engages when
        // no main thrust is being applied.
        if (SimBus.Instance.Propulsion.DampenersEnabled && _planet != null)
        {
            Vector3 toCenter = _planet.GlobalPosition - GlobalPosition;
            Vector3 gravDir = toCenter.Normalized();
            float distSq = toCenter.LengthSquared();
            float gravAccel = _planet.GM / distSq;
            Vector3 gravForce = gravDir * gravAccel * Mass;

            ApplyCentralForce(-gravForce);

            if (_thrustInput == 0f)
            {
                ApplyCentralForce(-LinearVelocity * LinearDampenerGain * Mass);
                _dampenerMode = "station_keep";
            }
            // During thrust near a planet: gravity countered, no velocity damping.
            // _dampenerMode stays "off".
        }
        else if (_thrustInput == 0f && SimBus.Instance.Propulsion.DampenersEnabled)
        {
            // No planet, not thrusting: cancel all velocity.
            ApplyCentralForce(-LinearVelocity * LinearDampenerGain * Mass);
            _dampenerMode = "station_keep";
        }

        // Atmospheric friction heating: applies regardless of engine state.
        // Uses density from _IntegrateForces this frame and current velocity.
        _engineTemp += _pendingAtmoDensity * LinearVelocity.Length() * AtmoHeatRate * dt;

        // Star proximity heat: applies only outside atmosphere (atmo wins when both
        // sources are active). Star.cs writes ExternalHeatRate each _PhysicsProcess;
        // zeroing here ensures it drops to 0 if Star leaves the scene mid-frame.
        if (!_inAtmosphere)
            _engineTemp += SimBus.Instance.Propulsion.ExternalHeatRate * dt;
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;

        // Passive radiative cooling always applies, even mid-burn, so heat
        // generation above is the net-of-cooling delta in practice.
        _engineTemp -= _engineTemp * CoolingRate * dt;
        _engineTemp = Mathf.Clamp(_engineTemp, 0f, 1000f);

        float effectiveMaxTemp = MaxEngineTemp * SimBus.Instance.Engineering.OverheatThresholdMultiplier;
        if (!_propulsionOverheated && _engineTemp >= effectiveMaxTemp)
        {
            _propulsionOverheated = true;
        }
        else if (_propulsionOverheated && _engineTemp < effectiveMaxTemp)
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
        if (!Input.IsActionJustPressed("toggle_dampeners")) return;
        // Cannot enable dampeners inside the atmosphere.
        if (_inAtmosphere && !SimBus.Instance.Propulsion.DampenersEnabled) return;
        SimBus.Instance.Propulsion.DampenersEnabled = !SimBus.Instance.Propulsion.DampenersEnabled;
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

        string dampenerLine = _dampenerMode == "station_keep"
            ? "Dampeners: ON — STATION KEEP"
            : "Dampeners: OFF";

        _debugLabel.Text =
            $"Velocity: {LinearVelocity.Length():0.0} m/s\n" +
            $"Alt: {_altitudeM:0} m\n" +
            dampenerLine + " (X to toggle)\n" +
            $"RCS: {(SimBus.Instance.Propulsion.RcsEnabled ? "ON" : "OFF")} (V to toggle)\n" +
            $"Mix: {mixLabel} (1=Economy, 2=Power)\n" +
            tempLine + "\n" +
            "? for controls";
    }

    // Updates SimBus.Propulsion telemetry each physics frame.
    // Acceleration is derived from the velocity delta since the last frame.
    private void PublishTelemetry(float dt)
    {
        float velocity = LinearVelocity.Length();
        float rawAccel = dt > 0f ? (velocity - _previousVelocity) / dt : 0f;
        _previousVelocity = velocity;
        // Low-pass filter: α = 0.15 smooths jitter at low speed while still
        // tracking real thrust changes within ~5 physics frames.
        _smoothedAcceleration = Mathf.Lerp(_smoothedAcceleration, rawAccel, 0.15f);

        SimBus.Instance.Propulsion.PublishTelemetry(
            propellantMix: _propellantMix,
            engineTemp: _engineTemp,
            overheated: _propulsionOverheated,
            propulsionDisabled: _propulsionOverheated || SimBus.Instance.Engineering.Engines.Disabled,
            velocity: velocity,
            accelerationMs2: _smoothedAcceleration,
            throttleInput: _thrustInput,
            reverseEnabled: _reverseEnabled,
            altitudeM: _altitudeM,
            dampenerMode: _dampenerMode);
    }

    // MQTT publish paths:
    //
    // PublishMqttState()     — immediate, on-change. Alerts only (discrete events
    //                          that should reach subscribers without delay).
    //
    // PublishMqttTelemetry() — rate-limited to TelemetryPublishRateHz. Propulsion
    //                          and FTL state (now includes continuous fields like
    //                          velocity and temp that change every frame, so
    //                          publishing every-physics-frame would flood the broker).

    private void PublishMqttState()
    {
        UpdateAlerts();
    }

    // ── Alert management ───────────────────────────────────────────────────

    private void UpdateAlerts()
    {
        var alerts = SimBus.Instance.Alerts;
        bool changed = false;

        // Propulsion overheat: warning, system "engines"
        bool wantOverheat = _propulsionOverheated;
        bool hasOverheat = _alertOverheatActive;
        if (wantOverheat && !hasOverheat)
        {
            alerts.Active.Add(new SimBus.AlertEntry(
                Id: "alert_engines_overheat",
                Severity: "warning",
                System: "engines",
                Message: "ENGINE OVERHEAT",
                TimestampS: (long)_missionTimeS));
            _alertOverheatActive = true;
            changed = true;
        }
        else if (!wantOverheat && hasOverheat)
        {
            alerts.Active.RemoveAll(a => a.Id == "alert_engines_overheat");
            _alertOverheatActive = false;
            changed = true;
        }

        // FTL charge aborted: caution, system "ftl"
        // Cleared when a new VECTOR press starts (i.e. phase transitions to Charging).
        bool wantFtlAbort = _ftlAborted;
        bool hasFtlAbort = _alertFtlAbortActive;
        if (wantFtlAbort && !hasFtlAbort)
        {
            alerts.Active.Add(new SimBus.AlertEntry(
                Id: "alert_ftl_aborted",
                Severity: "caution",
                System: "ftl",
                Message: "FTL CHARGE ABORTED",
                TimestampS: (long)_missionTimeS));
            _alertFtlAbortActive = true;
            changed = true;
        }
        else if (!wantFtlAbort && hasFtlAbort)
        {
            alerts.Active.RemoveAll(a => a.Id == "alert_ftl_aborted");
            _alertFtlAbortActive = false;
            changed = true;
        }

        // Hull impact — event-based, auto-clears after CollisionAlertDurationS.
        bool wantCollision = _collisionWantAlert;
        bool hasCollision = _alertCollisionActive;
        if (wantCollision && !hasCollision)
        {
            alerts.Active.Add(new SimBus.AlertEntry(
                Id: "alert_collision",
                Severity: "caution",
                System: "hull",
                Message: "HULL IMPACT",
                TimestampS: (long)_missionTimeS));
            _alertCollisionActive = true;
            changed = true;
        }
        else if (!wantCollision && hasCollision)
        {
            alerts.Active.RemoveAll(a => a.Id == "alert_collision");
            _alertCollisionActive = false;
            changed = true;
        }

        // Hull damage — distinct from alert_collision; fires when HP was taken.
        // Auto-clears after 5 s (timer driven by HandleCollision).
        bool wantHullDamage = _hullDamageWantAlert;
        bool hasHullDamage = _alertHullDamageActive;
        if (wantHullDamage && !hasHullDamage)
        {
            alerts.Active.Add(new SimBus.AlertEntry(
                Id: "alert_hull_damage",
                Severity: "caution",
                System: "hull",
                Message: "HULL IMPACT — DAMAGE TAKEN",
                TimestampS: (long)_missionTimeS));
            _alertHullDamageActive = true;
            changed = true;
        }
        else if (!wantHullDamage && hasHullDamage)
        {
            alerts.Active.RemoveAll(a => a.Id == "alert_hull_damage");
            _alertHullDamageActive = false;
            changed = true;
        }

        // Per-subsystem DISABLED alerts: warning, sticky until health > 0 via repair.
        var eng = SimBus.Instance.Engineering;
        foreach (var sys in eng.AllSystems)
        {
            string alertId = $"alert_system_disabled_{sys.Id}";
            bool wantDisabled = sys.Disabled;
            bool hasDisabled  = _alertDisabledSystems.Contains(sys.Id);
            if (wantDisabled && !hasDisabled)
            {
                alerts.Active.Add(new SimBus.AlertEntry(
                    Id: alertId,
                    Severity: "warning",
                    System: sys.Id,
                    Message: $"{sys.Id.Replace('_', ' ').ToUpperInvariant()} DISABLED",
                    TimestampS: (long)_missionTimeS));
                _alertDisabledSystems.Add(sys.Id);
                changed = true;
            }
            else if (!wantDisabled && hasDisabled)
            {
                alerts.Active.RemoveAll(a => a.Id == alertId);
                _alertDisabledSystems.Remove(sys.Id);
                changed = true;
            }
        }

        // Atmosphere entry — dampeners inoperable while below AtmosphereTopM.
        // Restores dampeners automatically on exit.
        bool wantAtmo = _inAtmosphere;
        bool hasAtmo = _alertAtmoActive;
        if (wantAtmo && !hasAtmo)
        {
            alerts.Active.Add(new SimBus.AlertEntry(
                Id: "alert_atmo_dampeners_inop",
                Severity: "caution",
                System: "propulsion",
                Message: $"PROXIMITY ALERT — {SimBus.Instance.Propulsion.SoiBody.ToUpperInvariant()}",
                TimestampS: (long)_missionTimeS));
            _alertAtmoActive = true;
            changed = true;
        }
        else if (!wantAtmo && hasAtmo)
        {
            alerts.Active.RemoveAll(a => a.Id == "alert_atmo_dampeners_inop");
            _alertAtmoActive = false;
            SimBus.Instance.Propulsion.DampenersEnabled = true;
            changed = true;
        }

        if (changed || _mqttAlertsNeedPublish)
        {
            SimBus.Instance.PublishCurrentAlerts();
            _mqttAlertsNeedPublish = false;
        }
    }

    // ── Rate-limited state publishes ──────────────────────────────────────

    // Sticky flag: set true by any repair change between publish ticks, cleared
    // after publish. Avoids losing changes that land on non-publish physics frames.
    private bool _engineeringNeedsPublish = false;

    private void PublishMqttTelemetry(float dt)
    {
        // Advance repair every physics frame regardless of publish rate, so
        // small rates accumulate correctly and aren't lost between ticks.
        if (SimBus.Instance.Engineering.UpdateRepair(dt, ReactorOutputKW, RepairKWPerHPPerSecond))
            _engineeringNeedsPublish = true;

        _telemetryPublishAccumulator += dt;
        float interval = 1f / Mathf.Max(TelemetryPublishRateHz, 0.01f);
        if (_telemetryPublishAccumulator < interval) return;
        _telemetryPublishAccumulator = 0f;

        var mqtt = SimBus.Instance.Mqtt;
        PublishPropulsionState(mqtt);
        PublishFtlState(mqtt);

        if (_engineeringNeedsPublish)
        {
            SimBus.Instance.PublishEngineeringState();
            SimBus.Instance.PublishRepairQueue();
            _engineeringNeedsPublish = false;
        }
    }

    private void PublishPropulsionState(MqttTelemetryPublisher mqtt)
    {
        var p = SimBus.Instance.Propulsion;

        // Three engines share a single temperature sensor; split effective
        // thrust power equally across port/centre/starboard.
        float enginePowerEach = p.ThrottleInput * PowerPerEnginekW;
        int tempC = (int)p.EngineTemp;

        string payload = JsonSerializer.Serialize(new
        {
            // armed: no propulsion Arm state in sim yet -- placeholder (§3.1b batch 8)
            armed = false,
            throttle = MathF.Round(p.ThrottleInput, 3),
            mix = MathF.Round(p.PropellantMix, 3),
            rcs_enabled = p.RcsEnabled,
            dampeners_enabled = p.DampenersEnabled,
            dampener_mode = p.DampenerMode,
            reverse_enabled = p.ReverseEnabled,
            // ship_temp_c: max of all engine temps. Currently all engines share
            // one sensor, so this equals any single engine's reading.
            ship_temp_c = tempC,
            engines = new object[]
            {
                new { id = "port",      power_kw = (int)enginePowerEach, temp_c = tempC },
                new { id = "centre",    power_kw = (int)enginePowerEach, temp_c = tempC },
                new { id = "starboard", power_kw = (int)enginePowerEach, temp_c = tempC },
            },
            velocity_ms = MathF.Round(p.Velocity, 2),
            acceleration_ms2 = MathF.Round(p.AccelerationMs2, 2),
            altitude_m = MathF.Round(p.AltitudeM, 1),
            // collision_force_n: 0.0 at rest, peak impulse (N) for 3 s after impact.
            collision_force_n = MathF.Round(p.CollisionForceN, 1),
            // soi_body: planet SoiName within the loose distance threshold,
            // "Deep Space" beyond it (label only — gravity has no SOI cutoff).
            soi_body = p.SoiBody,
        });

        mqtt.Publish("coldorbit/output/propulsion/state", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    private void PublishFtlState(MqttTelemetryPublisher mqtt)
    {
        var ftl = SimBus.Instance.Ftl;

        // Destination string (null when not armed, per §3.1b). Names and
        // distances now come from the real Drift star map (batch 16).
        string? destination = ftl.Armed ? ftl.SelectedName : null;

        float rangeAu = ftl.Armed ? ftl.RangeAu : 0f;

        // power_kw: 0 at idle, nominal otherwise. No real power model yet.
        // power_max_kw: fixed placeholder (§3.1b batch 8).
        int powerKw = ftl.Phase == FtlPhase.Idle ? 0 : 340;
        const int PowerMaxKw = 500;

        string payload = JsonSerializer.Serialize(new
        {
            armed = ftl.Armed,
            phase = ftl.Phase.ToString().ToLowerInvariant(),
            progress = MathF.Round(ftl.Progress, 3),
            destination,
            range_au = MathF.Round(rangeAu, 2),
            signal_lag_s = MathF.Round(ftl.SignalLagS, 2),
            power_kw = powerKw,
            power_max_kw = PowerMaxKw,
        });

        mqtt.Publish("coldorbit/output/ftl/state", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
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
    //
    // Cooldown phase (batch 8 §2): after a completed jump or an abort the
    // drive enters Cooldown. Arm/VECTOR/Jump are no-ops during Cooldown so
    // the drive cannot be used as a panic button.
    private void HandleFtl(float dt)
    {
        var ftl = SimBus.Instance.Ftl;

        if (ftl.PendingAdminReset)
        {
            ftl.PendingAdminReset = false;
            _ftlPhase = FtlPhase.Idle;
            _ftlTimer = 0f;
            _ftlAborted = false;
        }

        bool inFlight = _ftlPhase == FtlPhase.Charging || _ftlPhase == FtlPhase.Jumping;

        if (inFlight && SimBus.Instance.Propulsion.IsPropulsionDisabled)
        {
            // Overheat abort: go to Cooldown (not Idle) so the drive is
            // inert for CooldownDuration before another attempt can be made.
            _ftlPhase = FtlPhase.Cooldown;
            _ftlTimer = 0f;
            _ftlAborted = true;
            ftl.Armed = false; // force re-arm before another attempt
        }
        else if (!ftl.Armed && _ftlPhase is FtlPhase.Charging or FtlPhase.Ready)
        {
            // Deliberate disarm cancels charge/ready -- goes straight to Idle,
            // not Cooldown, because no fault occurred (panel-control-designs.md).
            _ftlPhase = FtlPhase.Idle;
            _ftlTimer = 0f;
        }

        switch (_ftlPhase)
        {
            case FtlPhase.Idle:
                // Cooldown guards are already handled above; only act on VECTOR
                // when actually armed and idle, and FTL subsystem is not disabled.
                if (ftl.Armed && ftl.VectorRequested
                    && !SimBus.Instance.Engineering.Ftl.Disabled)
                {
                    // No obstruction/traffic model exists yet, so the
                    // jump-point clear check always passes -- see
                    // panel-control-designs.md "Open dependency".
                    _ftlPhase = FtlPhase.Charging;
                    _ftlTimer = 0f;
                    _ftlAborted = false; // clear abort flag on new attempt
                }
                break;

            case FtlPhase.Charging:
                // Charge rate scales with FTL subsystem health; damaged FTL charges slower.
                _ftlTimer += dt * SimBus.Instance.Engineering.FtlChargeRateMultiplier;
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
                    _ftlPhase = FtlPhase.Cooldown;
                    _ftlTimer = 0f;
                }
                break;

            case FtlPhase.Cooldown:
                _ftlTimer += dt;
                if (_ftlTimer >= FtlCooldownDuration)
                {
                    _ftlPhase = FtlPhase.Idle;
                    _ftlTimer = 0f;
                }
                break;
        }

        ftl.VectorRequested = false;
        ftl.JumpRequested = false;

        // Progress: 0→1 during Charging, 1.0 during Ready, 0 during Jumping,
        // 1→0 during Cooldown, 0 at Idle.
        float progress = _ftlPhase switch
        {
            FtlPhase.Charging => _ftlTimer / FtlChargeDuration,
            FtlPhase.Ready    => 1f,
            FtlPhase.Cooldown => 1f - (_ftlTimer / FtlCooldownDuration),
            _                 => 0f,
        };

        // Signal lag (batch 8 §3.1b):
        //   Charging  → ramps 0 → MaxSignalLagS
        //   Ready     → holds at MaxSignalLagS
        //   Jumping   → holds at MaxSignalLagS (peak, jump moment)
        //   Cooldown  → decays MaxSignalLagS → 0
        //   Idle      → 0
        _ftlSignalLagS = _ftlPhase switch
        {
            FtlPhase.Charging => (_ftlTimer / FtlChargeDuration) * MaxSignalLagS,
            FtlPhase.Ready    => MaxSignalLagS,
            FtlPhase.Jumping  => MaxSignalLagS,
            FtlPhase.Cooldown => (1f - _ftlTimer / FtlCooldownDuration) * MaxSignalLagS,
            _                 => 0f,
        };

        // ChargeProgress / JumpProgress kept for ControlPanelsWindow status label.
        bool chargedOrLater = _ftlPhase is FtlPhase.Ready or FtlPhase.Jumping or FtlPhase.Cooldown;
        float chargeProgress = _ftlPhase == FtlPhase.Charging
            ? _ftlTimer / FtlChargeDuration
            : (chargedOrLater ? 1f : 0f);
        float jumpProgress = _ftlPhase == FtlPhase.Jumping
            ? _ftlTimer / FtlJumpDuration
            : (_ftlPhase == FtlPhase.Cooldown ? 1f : 0f);

        ftl.PublishTelemetry(_ftlPhase, chargeProgress, jumpProgress, progress, _ftlSignalLagS, _ftlAborted);
    }

    private void ExecuteJump(int destinationIndex)
    {
        var ftl = SimBus.Instance.Ftl;
        var dest = new DriftData.Destination(ftl.SelectedSystemId, ftl.SelectedPlanetIndex, ftl.SelectedName);
        SceneManager.Instance.LoadSoI(dest, LinearVelocity);
        // SceneManager places ship at SpawnPosition and zeroes AngularVelocity.
    }

    private void RegisterKeyAction(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
    }
}
