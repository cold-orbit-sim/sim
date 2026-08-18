using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MQTTnet.Protocol;

namespace ColdOrbit.SimCore;

// Decoupling layer between the sim (PlayerShip) and the control-panel UI,
// which lives in a separate OS Window and has no scene-tree reference to
// the ship. Shape anticipates the eventual MQTT-driven control model:
// panels/keyboard publish commands here for the sim to read, and the sim
// publishes telemetry here for panels to read -- the same split a panel
// will have once it's talking over MQTT topics instead of this bus.
//
// Grouped into one nested class per panel (Propulsion, Ftl, Touchscreen)
// rather than a flat property bag, since each wired panel adds its own
// commands and telemetry and that would get unwieldy flattened.
//
// Autoloaded as a script singleton (see project.godot [autoload]), so
// SimBus.Instance is available to any node from _Ready() onward.
public partial class SimBus : Node
{
    [Export] public string MqttBrokerHost { get; set; } = "localhost";
    [Export] public int MqttBrokerPort { get; set; } = 1883;

    public static SimBus Instance { get; private set; }

    public PropulsionState Propulsion { get; } = new();
    public FtlState Ftl { get; } = new();
    public TouchscreenState Touchscreen { get; } = new();
    public AlertsState Alerts { get; } = new();
    public CameraState Cameras { get; } = new();
    public EngineeringState Engineering { get; } = new();
    public MqttTelemetryPublisher Mqtt { get; private set; }

    // Set by PlayerShip._Ready from its [Export]. Published retained on every
    // broker connect so reconnecting displays always see the current callsign.
    public string ShipCallsign { get; set; } = "Cold Orbit";

    // Set by Planet._Ready. Read by the admin panel (gravity override, planet
    // constants) and PlayerShip (via its own cached reference). Main-thread only.
    public Planet? Planet { get; set; }

    // Set by PlayerShip._Ready. Read by SceneManager and Star to position/heat
    // the ship without holding a scene-tree reference to PlayerShip.
    public PlayerShip PlayerShipNode { get; set; }

    // Pending SurfaceGravity value from the admin panel, applied on the Godot
    // main thread in _Process (see AdminSetPlanetGravity).
    private float? _pendingPlanetGravity;

    // Default loadout: three utility tools in slots 1-3, slot 4 empty.
    // Overwritten on receipt of coldorbit/input/ship/loadout.
    public HardpointSlot[] Hardpoints { get; } = new HardpointSlot[4]
    {
        new() { Category = "utility_tool", Name = "Mining Laser" },
        new() { Category = "utility_tool", Name = "Cutting/Welding Torch", Mode = "weld" },
        new() { Category = "utility_tool", Name = "Grapple/Winch Rig" },
        new() { Category = "empty",        Name = null },
    };

    private float _hardpointTelemetryAccumulator;

    private static readonly HashSet<string> ValidTouchscreenModes = new()
    {
        "engineering", "propulsion", "ftl", "map", "turrets", "missiles", "comms", "hardpoints",
    };

    private static readonly HashSet<string> ValidCameraViews = new()
    {
        "forward", "aft", "chase", "dorsal", "ventral", "docking", "damage",
    };

    // Godot user:// path for persisting sim preferences across restarts.
    private const string PrefsPath = "user://sim_prefs.cfg";

    public override void _Ready()
    {
        Instance = this;
        Touchscreen.Mode = LoadTouchscreenMode();
        Mqtt = new MqttTelemetryPublisher(MqttBrokerHost, MqttBrokerPort);

        // Register subscription and wire events before Start() so the
        // filters and callbacks are in place before the first connect fires.
        Mqtt.Subscribe("coldorbit/input/touchscreen/+");
        Mqtt.Subscribe("coldorbit/input/cameras/+");
        Mqtt.Subscribe("coldorbit/input/comms/master_warn");
        Mqtt.Subscribe("coldorbit/input/comms/master_caut");
        Mqtt.Subscribe("coldorbit/input/alerts/acknowledge");
        Mqtt.Subscribe("coldorbit/input/ship/loadout");
        Mqtt.Subscribe("coldorbit/input/ftl/command");
        Mqtt.Subscribe("coldorbit/input/hardpoints/+/arm");
        Mqtt.Subscribe("coldorbit/input/hardpoints/+/softkey");
        Mqtt.Subscribe("coldorbit/input/hardpoints/+/encoder_a");
        Mqtt.Subscribe("coldorbit/input/hardpoints/+/encoder_b");
        Mqtt.MessageReceived += OnMqttMessageReceived;
        Mqtt.Connected += OnMqttConnected;

        Mqtt.Start();
    }

    public override void _ExitTree()
    {
        Mqtt?.Stop();
    }

    // Publishes hardpoint telemetry at 10 Hz from the Godot main thread.
    public override void _Process(double delta)
    {
        // Apply admin planet-gravity overrides on the main thread rather than
        // from the UI callback -- SurfaceGravity is also read on the physics
        // step (PlayerShip._IntegrateForces via GM).
        if (_pendingPlanetGravity.HasValue)
        {
            if (Planet != null) Planet.SurfaceGravity = _pendingPlanetGravity.Value;
            _pendingPlanetGravity = null;
        }

        const float hz = 10f;
        _hardpointTelemetryAccumulator += (float)delta;
        if (_hardpointTelemetryAccumulator < 1f / hz) return;
        _hardpointTelemetryAccumulator = 0f;
        for (int slot = 1; slot <= 4; slot++)
            PublishHardpointTelemetry(slot);
    }

    // Called on the MQTT background thread whenever a message arrives on any
    // subscribed topic.
    private void OnMqttMessageReceived(string topic, string payload)
    {
        // Route topics that don't carry a `state` field before the common parse.
        if (topic == "coldorbit/input/ship/loadout")
        {
            HandleLoadoutConfirm(payload);
            return;
        }

        const string hpPrefix = "coldorbit/input/hardpoints/";
        if (topic.StartsWith(hpPrefix, StringComparison.Ordinal))
        {
            HandleHardpointInput(topic.Substring(hpPrefix.Length), payload);
            return;
        }

        // FTL nav uses a bespoke payload (no `state` field) on the command
        // topic: an `armed` bool and/or a `dest_action` (prev/next) selection
        // change may appear together or separately.
        if (topic == "coldorbit/input/ftl/command")
        {
            HandleFtlCommand(payload);
            return;
        }

        // All remaining topics use a `state` field.
        int state;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetInt32() : -1;
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"SimBus: malformed payload on {topic}: {ex.Message}");
            return;
        }

        const string touchscreenPrefix = "coldorbit/input/touchscreen/";
        if (topic.StartsWith(touchscreenPrefix, StringComparison.Ordinal))
        {
            if (state != 1) return;
            var mode = topic.Substring(touchscreenPrefix.Length);
            if (!ValidTouchscreenModes.Contains(mode))
            {
                GD.PrintErr($"SimBus: unknown touchscreen mode '{mode}' on topic {topic}");
                return;
            }
            Touchscreen.Mode = mode;
            SaveTouchscreenMode(mode);
            Mqtt.Publish(
                "coldorbit/output/touchscreen/mode",
                mode,
                MqttQualityOfServiceLevel.AtLeastOnce,
                retain: true);
            return;
        }

        const string camerasPrefix = "coldorbit/input/cameras/";
        if (topic.StartsWith(camerasPrefix, StringComparison.Ordinal))
        {
            if (state != 1) return;
            var view = topic.Substring(camerasPrefix.Length);
            if (!ValidCameraViews.Contains(view))
            {
                GD.PrintErr($"SimBus: unknown camera view '{view}' on topic {topic}");
                return;
            }
            Cameras.PendingView = view;
            return;
        }

        // Alert-acknowledgement topics — act on press only (state:0 is no-op).
        if (state != 1) return;

        switch (topic)
        {
            case "coldorbit/input/comms/master_warn":
                // Master Warn acks both warnings and cautions (higher-severity button
                // implies pilot has seen the worst — §3.1b).
                AcknowledgeAlerts(a => a.Severity == "warning" || a.Severity == "caution");
                PublishCurrentAlerts();
                break;
            case "coldorbit/input/comms/master_caut":
                AcknowledgeAlerts(a => a.Severity == "caution");
                PublishCurrentAlerts();
                break;
            case "coldorbit/input/alerts/acknowledge":
                AcknowledgeAlerts(_ => true);
                PublishCurrentAlerts();
                break;
            default:
                GD.PrintErr($"SimBus: unhandled topic {topic}");
                break;
        }
    }

    // FTL panel command. Bespoke payload; both fields are optional and may
    // arrive together:
    //   { armed: bool }                     — arm/disarm the drive
    //   { dest_action: "prev" | "next" }    — cycle the flat destination list
    // A selection change republishes the resolved nav target and system detail.
    private void HandleFtlCommand(string payload)
    {
        bool selectionChanged = false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("armed", out var a) &&
                (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False))
            {
                Ftl.Armed = a.GetBoolean();
            }

            // Destination can only be (re-)selected while idle -- once VECTOR
            // locks it in, prev/next stop having an effect (mirrors the dev
            // panel disabling the nav buttons when Phase != Idle).
            if (root.TryGetProperty("dest_action", out var d) && d.ValueKind == JsonValueKind.String
                && Ftl.Phase is FtlPhase.Idle)
            {
                switch (d.GetString())
                {
                    case "prev": Ftl.CycleDestination(-1); selectionChanged = true; break;
                    case "next": Ftl.CycleDestination(1);  selectionChanged = true; break;
                    default:
                        GD.PrintErr($"SimBus: unknown ftl dest_action '{d.GetString()}'");
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"SimBus: malformed ftl/command payload: {ex.Message}");
            return;
        }

        if (selectionChanged)
        {
            PublishFtlSystem();
            PublishFtlNavTarget();
        }
    }

    // Parses loadout confirm and updates Hardpoints[]. Resets all operational
    // state (armed/active/mode/attached and all category-specific fields) on
    // each slot that changes so stale state doesn't bleed across loadouts.
    private void HandleLoadoutConfirm(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("slots", out var slots)) return;
            foreach (var prop in slots.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out int slotNum) || slotNum < 1 || slotNum > 4) continue;
                var v = prop.Value;
                var hp = Hardpoints[slotNum - 1];
                hp.Category = v.TryGetProperty("category", out var cat) ? cat.GetString() ?? "empty" : "empty";
                hp.Name = v.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null
                    ? name.GetString() : null;
                // Base reset
                hp.Armed     = false;
                hp.Active    = false;
                hp.Intensity = 0f;
                hp.Mode      = hp.Name == "Cutting/Welding Torch" ? "weld" : null;
                hp.Attached  = null;
                // Cargo/Storage reset
                hp.FillPct   = 0f;
                hp.Contents  = null;
                hp.TempC     = null;
                hp.TempMin   = null;
                hp.TempMax   = null;
                // Sensor/EW reset
                hp.ScannerModeActive = false;
                hp.ScannerModeBeam   = false;
                hp.ScannerBearing    = 0f;
                hp.StealthOn         = false;
                // Defense reset
                hp.ShieldOn             = false;
                hp.ShieldSelectedFacing = "fore";
                hp.ShieldStrengths      = new() { {"fore",0.5f},{"aft",0.5f},{"port",0.5f},{"starboard",0.5f} };
                hp.PdEngaged            = false;
                hp.MissileLockWarning   = false;
                hp.DecoyCount           = 12;
                PublishHardpointModule(slotNum);
            }
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"SimBus: malformed loadout payload: {ex.Message}");
        }
    }

    // Routes hardpoint input sub-topics: <slot>/arm, <slot>/softkey,
    // <slot>/encoder_a, <slot>/encoder_b.
    private void HandleHardpointInput(string subPath, string payload)
    {
        var sep = subPath.IndexOf('/');
        if (sep < 0 || !int.TryParse(subPath.Substring(0, sep), out int slot) || slot < 1 || slot > 4)
        {
            GD.PrintErr($"SimBus: invalid hardpoint topic: hardpoints/{subPath}");
            return;
        }
        string subTopic = subPath.Substring(sep + 1);
        var hp = Hardpoints[slot - 1];

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            switch (subTopic)
            {
                case "arm":
                    int armState = root.TryGetProperty("state", out var s) ? s.GetInt32() : -1;
                    hp.Armed = armState == 1;
                    PublishHardpointModule(slot);
                    break;

                case "softkey":
                    if (!hp.Armed) return;
                    int skState = root.TryGetProperty("state", out var ss) ? ss.GetInt32() : -1;
                    if (skState != 1) return; // press only
                    string key = root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    HandleSoftkey(hp, key, slot);
                    break;

                case "encoder_a":
                    int deltaA = root.TryGetProperty("delta", out var da) ? da.GetInt32() : 0;
                    HandleEncoderA(hp, deltaA, slot);
                    break;

                case "encoder_b":
                    int deltaB = root.TryGetProperty("delta", out var db) ? db.GetInt32() : 0;
                    HandleEncoderB(hp, deltaB, slot);
                    break;

                default:
                    GD.PrintErr($"SimBus: unknown hardpoint sub-topic '{subTopic}'");
                    break;
            }
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"SimBus: malformed hardpoint payload on {subTopic}: {ex.Message}");
        }
    }

    // Dispatches a soft-key press to the correct category handler.
    // All handlers guard on Armed == true (checked by caller).
    private void HandleSoftkey(HardpointSlot hp, string key, int slot)
    {
        switch (hp.Category)
        {
            case "utility_tool":
                HandleSoftkeyUtilityTool(hp, key, slot);
                break;
            case "cargo_storage":
                // All SKs ignored — no cargo-manipulation inputs yet.
                break;
            case "sensor_ew":
                HandleSoftkeySensorEW(hp, key, slot);
                break;
            case "defense":
                HandleSoftkeyDefense(hp, key, slot);
                break;
            default:
                GD.Print($"SimBus: softkey {key} slot {slot} (category={hp.Category}) — ignored");
                break;
        }
    }

    private void HandleSoftkeyUtilityTool(HardpointSlot hp, string key, int slot)
    {
        switch (key)
        {
            case "SK5":                             // ON / LAUNCH
                hp.Active = true;
                if (hp.Name == "Grapple/Winch Rig") hp.Attached = true;
                break;
            case "SK6":                             // OFF / RELEASE
                hp.Active = false;
                if (hp.Name == "Grapple/Winch Rig") hp.Attached = false;
                break;
            case "SK3":                             // WELD
                if (hp.Name == "Cutting/Welding Torch") hp.Mode = "weld";
                break;
            case "SK7":                             // CUT
                if (hp.Name == "Cutting/Welding Torch") hp.Mode = "cut";
                break;
            case "SK1": case "SK2": case "SK4": case "SK8":  // directional aim
                GD.Print($"SimBus: directional aim {key} slot {slot} (no state modelled this batch)");
                break;
            default:
                GD.PrintErr($"SimBus: unknown softkey '{key}' slot {slot} (utility_tool)");
                break;
        }
        PublishHardpointModule(slot);
    }

    private void HandleSoftkeySensorEW(HardpointSlot hp, string key, int slot)
    {
        switch (hp.Name)
        {
            case "Long-range Scanner Array":
                switch (key)
                {
                    case "SK5":
                        hp.ScannerModeActive = !hp.ScannerModeActive;
                        break;
                    case "SK6":
                        if (hp.ScannerModeActive)
                            hp.ScannerModeBeam = !hp.ScannerModeBeam;
                        else
                            GD.Print($"SimBus: SK6 slot {slot} (Scanner Array) — ignored, not in Active mode");
                        break;
                    default:
                        GD.Print($"SimBus: softkey {key} slot {slot} (Scanner Array) — ignored");
                        break;
                }
                break;

            case "Prospecting Suite":
                switch (key)
                {
                    case "SK5":
                        // SCAN triggered — no gameplay outcome yet
                        GD.Print($"SimBus: SK5 slot {slot} (Prospecting Suite) — SCAN triggered (no gameplay outcome)");
                        break;
                    case "SK6":
                        StepProspectingIndex(hp, -1);
                        break;
                    case "SK7":
                        StepProspectingIndex(hp, +1);
                        break;
                    default:
                        GD.Print($"SimBus: softkey {key} slot {slot} (Prospecting Suite) — ignored");
                        break;
                }
                break;

            case "Stealth/ECM Package":
                switch (key)
                {
                    case "SK5":
                        hp.StealthOn = !hp.StealthOn;
                        break;
                    default:
                        GD.Print($"SimBus: softkey {key} slot {slot} (Stealth/ECM) — ignored");
                        break;
                }
                break;

            default:
                GD.Print($"SimBus: softkey {key} slot {slot} (sensor_ew/{hp.Name}) — ignored");
                break;
        }
        PublishHardpointModule(slot);
    }

    private void HandleSoftkeyDefense(HardpointSlot hp, string key, int slot)
    {
        switch (hp.Name)
        {
            case "Deflector Shield Generator":
                switch (key)
                {
                    case "SK1": hp.ShieldSelectedFacing = "fore";      break;
                    case "SK2": hp.ShieldSelectedFacing = "aft";       break;
                    case "SK3": hp.ShieldSelectedFacing = "port";      break;
                    case "SK4": hp.ShieldSelectedFacing = "starboard"; break;
                    case "SK5": hp.ShieldOn = !hp.ShieldOn;            break;
                    default:
                        GD.Print($"SimBus: softkey {key} slot {slot} (Shield Generator) — ignored");
                        break;
                }
                break;

            case "Point-Defense Turret Pod":
                switch (key)
                {
                    case "SK5": hp.PdEngaged = !hp.PdEngaged; break;
                    default:
                        GD.Print($"SimBus: softkey {key} slot {slot} (PD Turret) — ignored");
                        break;
                }
                break;

            case "Decoy/Flare Dispenser":
                switch (key)
                {
                    case "SK5":
                        if (hp.DecoyCount > 0) hp.DecoyCount--;
                        break;
                    default:
                        GD.Print($"SimBus: softkey {key} slot {slot} (Decoy/Flare) — ignored");
                        break;
                }
                break;

            default:
                GD.Print($"SimBus: softkey {key} slot {slot} (defense/{hp.Name}) — ignored");
                break;
        }
        PublishHardpointModule(slot);
    }

    // Encoder A: primary axis. Intensity for most modules, index-step for
    // Prospecting Suite, shield-strength-on-selected-facing for Shield Generator.
    // Publishes module state only for Shield Generator (shield_strengths is in
    // module state); all other changes are reflected at the 10 Hz telemetry cadence.
    private void HandleEncoderA(HardpointSlot hp, int delta, int slot)
    {
        switch (hp.Name)
        {
            case "Mining Laser":
            case "Cutting/Welding Torch":
            case "Grapple/Winch Rig":
            case "Long-range Scanner Array":
            case "Stealth/ECM Package":
                hp.Intensity = Mathf.Clamp(hp.Intensity + delta * 0.05f, 0f, 1f);
                break;

            case "Prospecting Suite":
                StepProspectingIndex(hp, delta);
                break;

            case "Deflector Shield Generator":
                var f = hp.ShieldSelectedFacing;
                hp.ShieldStrengths[f] = Mathf.Clamp(hp.ShieldStrengths[f] + delta * 0.05f, 0f, 1f);
                PublishHardpointModule(slot);
                break;

            // Point-Defense Turret, Decoy/Flare, cargo, empty: ignored.
        }
    }

    // Encoder B: secondary axis per module. Scanner bearing (Active+Beam only),
    // ore filter index for Prospecting, frequency for Stealth. All others ignored.
    // Scanner bearing triggers a module publish even though bearing is not in
    // the module state payload (keeps updated_at fresh per spec note).
    private void HandleEncoderB(HardpointSlot hp, int delta, int slot)
    {
        switch (hp.Name)
        {
            case "Long-range Scanner Array":
                if (!hp.ScannerModeActive || !hp.ScannerModeBeam)
                {
                    GD.Print($"SimBus: encoder_b slot {slot} (Scanner Array) — ignored, not Active+Beam");
                    return;
                }
                hp.ScannerBearing = (hp.ScannerBearing + delta) % 360f;
                if (hp.ScannerBearing < 0f) hp.ScannerBearing += 360f;
                PublishHardpointModule(slot);
                break;

            case "Prospecting Suite":
                StepProspectingIndex(hp, delta);
                break;

            case "Stealth/ECM Package":
                hp.Intensity = Mathf.Clamp(hp.Intensity + delta * 0.05f, 0f, 1f);
                break;

            default:
                GD.Print($"SimBus: encoder_b slot {slot} ({hp.Name ?? hp.Category}) — ignored");
                break;
        }
    }

    // Steps the Prospecting Suite ore-filter index by delta (±1 per detent).
    // Intensity is stored as a 0–1 float where the integer index = round(val×4).
    private static void StepProspectingIndex(HardpointSlot hp, int delta)
    {
        int cur = (int)MathF.Round(hp.Intensity * 4f);
        hp.Intensity = Math.Clamp(cur + delta, 0, 4) / 4.0f;
    }

    // Sets Acknowledged = true on all active alerts matching the predicate.
    // Called from the MQTT background thread; matches the threading model already
    // established for Alerts.Active access in this class.
    private void AcknowledgeAlerts(Func<AlertEntry, bool> predicate)
    {
        var active = Alerts.Active;
        for (int i = 0; i < active.Count; i++)
        {
            if (predicate(active[i]) && !active[i].Acknowledged)
                active[i] = active[i] with { Acknowledged = true };
        }
    }

    // Publishes the current mode as the retained value immediately after each
    // broker connection. On first connect the default is "hardpoints" (§3.7).
    // On reconnect it re-asserts whatever is currently active, so a broker
    // restart doesn't lose the retained state.
    //
    // Also publishes stub payloads for systems with no real sim logic yet (batch 8).
    // Stubs are static mocks -- they give the touchscreen views something to render
    // rather than sitting in a "waiting" state. PlayerShip re-publishes real
    // propulsion/FTL/alerts state separately on its telemetry cadence.
    private void OnMqttConnected()
    {
        Mqtt.Publish(
            "coldorbit/output/touchscreen/mode",
            Touchscreen.Mode,
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true);

        PublishAdminOverrideShipCallsign(ShipCallsign);
        PublishCameraState();
        PublishStartupStubs();
        // Publish real hardpoint module state (replaces stubs from batch 8).
        for (int slot = 1; slot <= 4; slot++)
            PublishHardpointModule(slot);
        PublishCurrentAlerts();
        // Publish the "no selection yet" marker first, then immediately the
        // resolved default selection (Kerath star) and its system detail, so a
        // fresh/reconnected Map view sees both the reset and the current target.
        PublishFtlNavTargetNone();
        PublishFtlSystem();
        PublishFtlNavTarget();
    }

    // Publishes the active camera view as the retained value. Called on startup,
    // on every view change (from CameraController.SwitchTo), and on broker
    // reconnect so a subscriber always sees the current view immediately.
    internal void PublishCameraState()
    {
        Mqtt.Publish(
            "coldorbit/output/cameras/active",
            Cameras.ActiveView,
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true);
    }

    // Republishes the current alerts array after a broker reconnect, so
    // a subscriber (the touchscreen) that reconnected after the broker
    // restarted sees the correct alert state immediately.
    internal void PublishCurrentAlerts()
    {
        var payload = JsonSerializer.Serialize(Alerts.Active);
        Mqtt.Publish("coldorbit/output/alerts", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // Resolved FTL nav target for the touchscreen Map view (§3.1b). Retained so
    // a fresh subscriber sees the current selection immediately. Published on
    // every selection change, at startup, and on broker reconnect.
    internal void PublishFtlNavTarget()
    {
        var sys = Ftl.SelectedSystem;
        float dist = Ftl.RangeAu;

        object payload;
        if (Ftl.IsStarSelected)
        {
            payload = new
            {
                type = "star",
                system_id = sys.Id,
                name = sys.StarName,
                star_type = sys.StarType,
                planet_count = sys.Planets.Length,
                distance_au = MathF.Round(dist, 1),
                spool_time_s = (int)Ftl.SpoolTimeSeconds,
            };
        }
        else
        {
            payload = new
            {
                type = "planet",
                system_id = sys.Id,
                name = sys.Planets[Ftl.SelectedPlanetIndex].Name,
                system_name = sys.StarName,
                star_type = sys.StarType,
                distance_au = MathF.Round(dist, 1),
                spool_time_s = (int)Ftl.SpoolTimeSeconds,
            };
        }

        Mqtt.Publish("coldorbit/output/ftl/target", JsonSerializer.Serialize(payload),
                     MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // Startup-only "no selection yet" marker on ftl/target. The real default
    // (Kerath star) follows immediately after, per the batch 16 contract.
    private void PublishFtlNavTargetNone()
    {
        Mqtt.Publish("coldorbit/output/ftl/target",
                     JsonSerializer.Serialize(new { type = "none" }),
                     MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // System detail for the touchscreen Map view (§3.1b). Only populated when a
    // planet is selected — the Map view uses it to draw the in-system layout.
    // A star (or no) selection publishes a null system_id so the view clears.
    // Retained; published alongside ftl/target on every selection change.
    internal void PublishFtlSystem()
    {
        object payload;
        if (!Ftl.IsStarSelected)
        {
            var sys = Ftl.SelectedSystem;
            payload = new
            {
                system_id = sys.Id,
                star_name = sys.StarName,
                star_type = sys.StarType,
                planets = sys.Planets.Select(p => new { name = p.Name }).ToArray(),
            };
        }
        else
        {
            payload = new { system_id = (string?)null };
        }

        Mqtt.Publish("coldorbit/output/ftl/system", JsonSerializer.Serialize(payload),
                     MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // ── Startup stubs ────────────────────────────────────────────────────────
    // MOCK DATA — none of the systems below have real sim logic yet (batch 8).
    // Published on every broker connection so the touchscreen views render
    // something rather than a "waiting" state. Replace each system stub with
    // real telemetry when the corresponding sim logic is added.
    private void PublishStartupStubs()
    {
        PublishEngineeringState();
        PublishRepairQueue();
        PublishCommsStubs();
        PublishTurretStubs();
        PublishMissileStubs();
        // Hardpoint stubs retired — real publish happens in OnMqttConnected.

        // TEMPORARY: always unlocked so the loadout screen is testable
        // without a game-state trigger. Replace with real lock/unlock logic.
        Mqtt.Publish(
            "coldorbit/output/ship/loadout-unlocked",
            "false",
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true);
    }

    // Publishes real subsystem state for all nine systems. Called on broker connect
    // and whenever health, disabled, or effects change (damage or repair).
    internal void PublishEngineeringState()
    {
        var eng = Engineering;
        foreach (var sys in eng.AllSystems)
        {
            string? powerUnit = sys.PowerAllocatedKW.HasValue ? "kW" : null;

            // repair_status mirrors the physical panel LED:
            //   "healthy"   — health == 100 (LED off)
            //   "damaged"   — health < 100, not in repair queue (LED red)
            //   "queued"    — in repair queue but not index 0 (LED orange)
            //   "repairing" — index 0 in repair queue (LED green)
            int qPos = eng.RepairQueue.IndexOf(sys.Id);
            string repairStatus = sys.Health >= 100 ? "healthy"
                : qPos == 0                         ? "repairing"
                : qPos > 0                          ? "queued"
                :                                     "damaged";

            string payload = JsonSerializer.Serialize(new
            {
                system           = sys.Id,
                health           = sys.Health,
                power_allocated  = sys.PowerAllocatedKW,
                power_unit       = powerUnit,
                power_max        = sys.PowerMaxKW,
                disabled         = sys.Disabled,
                effects          = eng.BuildEffects(sys),
                repair_status    = repairStatus,
                repair_eta_seconds = sys.RepairEtaSeconds.HasValue
                    ? (int?)((int)(sys.RepairEtaSeconds.Value + 0.5f))
                    : null,
            });
            Mqtt.Publish(
                $"coldorbit/output/engineering/{sys.Id}/state",
                payload,
                MqttQualityOfServiceLevel.AtLeastOnce,
                retain: true);
        }
    }

    private void PublishCommsStubs()
    {
        // MOCK — §3.1b comms contract. Replace with real mission log when comms system exists.
        var logPayload = JsonSerializer.Serialize(new object[]
        {
            new { id = "msg_001", direction = "incoming", sender = "Harlan Voss",
                  text = "Cold Orbit, this is Voss. You in position?", timestamp_s = 3600 },
            new { id = "msg_002", direction = "outgoing", sender = "player",
                  text = "Affirmative. Holding at waypoint delta.", timestamp_s = 3618 },
        });
        Mqtt.Publish("coldorbit/output/comms/log", logPayload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);

        var targetsPayload = JsonSerializer.Serialize(new object[]
        {
            new { id = "contact_001", name = "Harlan Voss", alliance = "Independent",
                  vessel_class = "Light Freighter", range_m = 1240 },
        });
        Mqtt.Publish("coldorbit/output/comms/targets", targetsPayload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    private void PublishTurretStubs()
    {
        // MOCK — §3.1b turrets contract. Replace with real turret state when turret system exists.
        foreach (var turret in new[] { "dorsal", "ventral" })
        {
            var payload = JsonSerializer.Serialize(new
            {
                turret,
                armed = false,
                fire_mode = "lethal",
                lock_state = "none",
                bearing_deg = (float?)null,
                target_name = (string?)null,
                target_class = (string?)null,
                target_alliance = (string?)null,
                target_range_m = (int?)null,
                ammo_loaded = "Kinetic Slug",
                ammo_remaining = new object[]
                {
                    new { type = "Kinetic Slug", count = 142 },
                    new { type = "EMP Round",    count = 28  },
                },
                heat = 0.0f,
            });
            Mqtt.Publish($"coldorbit/output/turrets/{turret}/state", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
        }
    }

    private void PublishMissileStubs()
    {
        // MOCK — §3.1b missiles contract. Replace with real missile state when missile system exists.
        foreach (var tube in new[] { "fore_port", "fore_starboard", "aft_port", "aft_starboard" })
        {
            var payload = JsonSerializer.Serialize(new
            {
                tube,
                armed = false,
                status = "loaded",
                missile_type = (string?)"Seeking",
                lock_state = "none",
                target_name = (string?)null,
                target_class = (string?)null,
                target_alliance = (string?)null,
                target_range_m = (int?)null,
            });
            Mqtt.Publish($"coldorbit/output/missiles/{tube}/state", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
        }
    }

    // Publishes the current repair queue as an ordered list. Called on broker
    // connect and whenever the queue or repair ETAs change.
    internal void PublishRepairQueue()
    {
        var eng = Engineering;
        var entries = new object[eng.RepairQueue.Count];
        for (int i = 0; i < eng.RepairQueue.Count; i++)
        {
            var sys = eng.GetById(eng.RepairQueue[i]);
            entries[i] = new
            {
                system             = sys.Id,
                status             = i == 0 ? "in_progress" : "queued",
                health             = sys.Health,
                repair_eta_seconds = sys.RepairEtaSeconds.HasValue
                    ? (int?)((int)(sys.RepairEtaSeconds.Value + 0.5f))
                    : null,
            };
        }
        Mqtt.Publish("coldorbit/output/repair/queue",
            JsonSerializer.Serialize(entries),
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // Publishes the retained module state for one hardpoint slot. Called on
    // any state change (arm, softkey, loadout confirm, admin override).
    // Per-module field inclusion follows the contract table — fields not listed
    // for a module are omitted entirely from the payload.
    public void PublishHardpointModule(int slot)
    {
        var hp = Hardpoints[slot - 1];
        string payload = JsonSerializer.Serialize(BuildModulePayload(hp, slot));
        Mqtt.Publish($"coldorbit/output/hardpoints/{slot}/module", payload,
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // Builds the per-module state dictionary. Each module type includes only
    // the fields listed in the contract table; all others are omitted.
    private static Dictionary<string, object?> BuildModulePayload(HardpointSlot hp, int slot)
    {
        var d = new Dictionary<string, object?>
        {
            ["slot"]       = slot,
            ["category"]   = hp.Category,
            ["name"]       = hp.Name,
            ["armed"]      = hp.Armed,
            ["updated_at"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        switch (hp.Name)
        {
            // utility_tool
            case "Cutting/Welding Torch":
                d["mode"] = hp.Mode;
                break;
            case "Grapple/Winch Rig":
                d["attached"] = hp.Attached;
                break;
            // Mining Laser: no extra fields

            // cargo_storage
            case "Standard Pod":
            case "Ore Hopper":
                d["fill_pct"]  = hp.FillPct;
                d["contents"]  = hp.Contents;
                break;
            case "Reefer Pod":
                d["fill_pct"]  = hp.FillPct;
                d["contents"]  = hp.Contents;
                d["temp_c"]    = hp.TempC;
                d["temp_min"]  = hp.TempMin;
                d["temp_max"]  = hp.TempMax;
                break;

            // sensor_ew
            case "Long-range Scanner Array":
                d["scanner_mode_active"] = hp.ScannerModeActive;
                d["scanner_mode_beam"]   = hp.ScannerModeBeam;
                break;
            case "Stealth/ECM Package":
                d["stealth_on"] = hp.StealthOn;
                break;
            // Prospecting Suite: no extra fields (ore filter index in telemetry only)

            // defense
            case "Deflector Shield Generator":
                d["shield_on"]              = hp.ShieldOn;
                d["shield_selected_facing"] = hp.ShieldSelectedFacing;
                d["shield_strengths"]       = hp.ShieldStrengths;
                break;
            case "Point-Defense Turret Pod":
                d["pd_engaged"] = hp.PdEngaged;
                break;
            case "Decoy/Flare Dispenser":
                d["missile_lock_warning"] = hp.MissileLockWarning;
                d["decoy_count"]          = hp.DecoyCount;
                break;

            // empty, unknown: no extra fields
        }

        return d;
    }

    // Publishes 10 Hz telemetry for one hardpoint slot. Cargo, point-defense, decoy,
    // and empty slots omit the publish entirely — the display ignores missing topics
    // better than stale zeroes.
    // Utility tool telemetry uses real units (kW / m) per the batch 13 contract
    // update (batch 12 published % which has been corrected here).
    private void PublishHardpointTelemetry(int slot)
    {
        var hp = Hardpoints[slot - 1];
        string label;
        float  value;
        string unit;
        int    min = 0;
        int    max;

        switch (hp.Name)
        {
            case "Mining Laser":
            case "Cutting/Welding Torch":
                label = "INTNS";
                value = MathF.Round(hp.Intensity * 500f, 1);
                unit  = "kW";
                max   = 500;
                break;
            case "Grapple/Winch Rig":
                label = "LEN";
                value = MathF.Round(hp.Intensity * 200f, 1);
                unit  = "m";
                max   = 200;
                break;
            case "Long-range Scanner Array":
                label = "RANGE";
                value = MathF.Round(hp.Intensity * 500f, 1);
                unit  = "km";
                max   = 500;
                break;
            case "Prospecting Suite":
                label = "IDX";
                value = MathF.Round(hp.Intensity * 4f);
                unit  = "";
                max   = 4;
                break;
            case "Stealth/ECM Package":
                label = "FREQ";
                value = MathF.Round(hp.Intensity * 100f, 1);
                unit  = "MHz";
                max   = 100;
                break;
            case "Deflector Shield Generator":
                label = "STR";
                value = MathF.Round(hp.ShieldStrengths[hp.ShieldSelectedFacing] * 100f, 1);
                unit  = "%";
                max   = 100;
                break;
            // Standard Pod, Reefer Pod, Ore Hopper, Point-Defense Turret Pod,
            // Decoy/Flare Dispenser, empty: omit telemetry entirely.
            default:
                return;
        }

        string payload = JsonSerializer.Serialize(new
        {
            slot, label, value, unit, min, max,
            active = hp.Active,
            mode   = hp.Mode,
        });
        Mqtt.Publish($"coldorbit/output/hardpoints/{slot}/telemetry", payload,
            MqttQualityOfServiceLevel.AtMostOnce, retain: false);
    }

    // ── State classes ────────────────────────────────────────────────────────

    public sealed class PropulsionState
    {
        // --- Commands: written by control panels (or keyboard), read by PlayerShip ---
        public bool DampenersEnabled { get; set; } = true;
        public bool RcsEnabled { get; set; } = false;
        public float MixTarget { get; set; } = 0f; // 0 = Economy, 1 = Power

        // --- Admin overrides (testing only, no MQTT publish) ---
        public float AdminThrustMultiplier { get; set; } = 1.0f;
        public bool AdminOvertempBypass { get; set; } = false;

        // --- Telemetry: written by PlayerShip each physics frame, read by control panels ---
        public float PropellantMix { get; private set; }
        public float EngineTemp { get; private set; }
        public bool Overheated { get; private set; }
        public float Velocity { get; private set; }
        public float AccelerationMs2 { get; private set; }
        public float ThrottleInput { get; private set; }  // 0–1, abs of current thrust axis
        public bool ReverseEnabled { get; private set; }  // true while reverse input is active

        // Altitude above the nearest planet's surface (m). Negative below the
        // surface (crash state). Written by PlayerShip each physics frame.
        public float AltitudeM { get; private set; }

        // SOI-body label set by SceneManager.LoadSoI when the active SoI scene
        // changes. Published to MQTT via p.SoiBody in PublishPropulsionState.
        // Label only — gravity has no SOI cutoff.
        public string SoiBody { get; set; } = "Deep Space";

        // Set each _PhysicsProcess by Star.cs when the ship is within the star's
        // heat zone. PlayerShip reads it in HandleThrust and zeros it after applying
        // so Star must re-set it every frame while the ship is in range.
        public float ExternalHeatRate { get; set; } = 0f;

        // Dampener mode, derived from physics each frame:
        //   "off"          — dampeners disabled or thrust active
        //   "station_keep" — dampeners on, low tangential speed (hovering)
        //   "orbit_hold"   — dampeners on, high tangential speed (arc-holding)
        public string DampenerMode { get; private set; } = "off";

        // True whenever propulsion is disabled, regardless of cause. Currently
        // only ever set from the overheat cutoff below, but named and read
        // independently of that so a future damage/sabotage system can set it
        // too without any reader (e.g. FTL's jump-abort interrupt) changing.
        public bool IsPropulsionDisabled { get; private set; }

        // Peak collision impulse (N) within the last 3 s. Zero when no recent impact.
        // Written by PlayerShip.HandleCollision; read by PublishPropulsionState and
        // the admin panel display. Placeholder until the damage system exists.
        public float CollisionForceN { get; set; } = 0f;

        // Set by AdminTriggerCollisionAlert(); PlayerShip.HandleCollision picks it up
        // on the next physics frame so the alert fires through the normal path.
        public float PendingAdminCollisionN { get; set; } = 0f;

        // Set by PlayerShip._Ready from its [Export]. Used by admin override
        // methods that need ship-config values without a PlayerShip reference.
        public float PowerPerEnginekW { get; set; } = 1500f;
        public float CollisionAlertThresholdN { get; set; } = 5000f;

        // Set by AdminResetToSpawn(); PlayerShip._PhysicsProcess teleports the
        // ship to origin and zeroes all velocity on the next physics frame.
        public bool PendingSpawnReset { get; set; } = false;

        // Set by AdminSimulateAtmosphere(); ORed into PlayerShip._inAtmosphere so
        // the atmosphere alert and dampener lockout can be tested without flying
        // to low altitude.
        public bool AdminAtmoSimulated { get; set; } = false;

        public void PublishTelemetry(
            float propellantMix, float engineTemp, bool overheated, bool propulsionDisabled,
            float velocity, float accelerationMs2, float throttleInput, bool reverseEnabled,
            float altitudeM, string dampenerMode)
        {
            PropellantMix = propellantMix;
            EngineTemp = engineTemp;
            Overheated = overheated;
            IsPropulsionDisabled = propulsionDisabled;
            Velocity = velocity;
            AccelerationMs2 = accelerationMs2;
            ThrottleInput = throttleInput;
            ReverseEnabled = reverseEnabled;
            AltitudeM = altitudeM;
            DampenerMode = dampenerMode;
        }
    }

    public sealed class FtlState
    {
        // Two-layer destination selection over the Drift star map (batch 16).
        // Long-range jumps target a star; in-system jumps target a planet, and
        // planets are only reachable in the system the ship is already in.
        public string SelectedSystemId { get; set; } = DefaultSystemId;
        public int SelectedPlanetIndex { get; set; } = -1; // -1 = star selected

        // Where the ship currently is. No real travel exists yet, so this is a
        // fixed default that PlayerShip re-exports for tweaking without a rebuild.
        public const string DefaultSystemId = "K";
        public string CurrentSystemId { get; set; } = DefaultSystemId;

        // Spool-up model: charge seconds = Base + distanceAu * PerAu.
        public float BaseChargeTime { get; set; } = 7.8f;
        public float ChargeTimePerDistanceUnit { get; set; } = 2.2f;

        public bool IsStarSelected => SelectedPlanetIndex < 0;

        public DriftData.StarSystem SelectedSystem => DriftData.GetSystem(SelectedSystemId);

        // Display name for the current selection: star name, or planet name
        // when drilled into a planet.
        public string SelectedName
            => IsStarSelected
                ? SelectedSystem.StarName
                : SelectedSystem.Planets[SelectedPlanetIndex].Name;

        // "Star" for a star selection, "Star / Planet" when drilled into a
        // planet. Used by the in-Godot dev panel label so both the system and
        // the planet are visible at a glance.
        public string SelectedDisplayName
            => IsStarSelected
                ? SelectedSystem.StarName
                : $"{SelectedSystem.StarName} / {SelectedSystem.Planets[SelectedPlanetIndex].Name}";

        // Straight-line range to the current selection, in AU, from the real
        // star chart. In-system (planet) targets share the star's coordinates,
        // so a small per-planet increment is added so each planet reads a
        // slightly different (short) range instead of collapsing to the star's.
        public float RangeAu
        {
            get
            {
                float baseAu = DriftData.DistanceAu(CurrentSystemId, SelectedSystemId);
                if (!IsStarSelected)
                    baseAu += 0.1f * (SelectedPlanetIndex + 1);
                return baseAu;
            }
        }

        // Charge/spool-up time for the current selection (Task 3).
        public float SpoolTimeSeconds => BaseChargeTime + RangeAu * ChargeTimePerDistanceUnit;

        // Index of the current selection in the shared flat destination list.
        public int DestinationIndex
            => DriftData.DestinationIndexOf(SelectedSystemId, SelectedPlanetIndex);

        // Move the selection by `direction` (+1 next, −1 prev) through the nav
        // cycle list, wrapping at both ends.
        // Cycle list: all 26 stars A-Z, with planets of the current system
        // inserted after their star. The current selection is skipped (you can't
        // jump to where you already are). Admin flat-picker uses SelectTo instead.
        public void CycleDestination(int direction)
        {
            var list = BuildNavList();
            int n = list.Length;
            if (n == 0) return;

            // Find the current selection in the list (included so we know our position).
            int cur = -1;
            for (int i = 0; i < n; i++)
            {
                if (list[i].SystemId == SelectedSystemId && list[i].PlanetIndex == SelectedPlanetIndex)
                { cur = i; break; }
            }
            // Fallback: current selection not in list (e.g. admin set a planet in another system).
            // Land on the current system's star as the position reference.
            if (cur < 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (list[i].SystemId == SelectedSystemId && list[i].PlanetIndex < 0)
                    { cur = i; break; }
                }
            }
            if (cur < 0) cur = 0;

            // Step, skipping the current selection itself.
            int next = (cur + direction + n) % n;
            while (list[next].SystemId == SelectedSystemId && list[next].PlanetIndex == SelectedPlanetIndex)
                next = (next + direction + n) % n;

            SelectTo(list[next]);
        }

        // Builds the nav cycle list: all 26 stars A-Z, with the planets of the
        // ship's current system inserted immediately after their star.
        private DriftData.Destination[] BuildNavList()
        {
            var result = new System.Collections.Generic.List<DriftData.Destination>();
            foreach (var s in DriftData.Systems)
            {
                result.Add(new DriftData.Destination(s.Id, -1, s.StarName));
                if (s.Id == CurrentSystemId)
                {
                    for (int p = 0; p < s.Planets.Length; p++)
                        result.Add(new DriftData.Destination(s.Id, p, s.Planets[p].Name));
                }
            }
            return result.ToArray();
        }

        // Point the selection at an explicit destination (used by the Admin
        // flat picker).
        public void SelectTo(DriftData.Destination d)
        {
            SelectedSystemId = d.SystemId;
            SelectedPlanetIndex = d.PlanetIndex;
        }

        // --- Commands: written by the FTL panel, read by PlayerShip ---
        public bool Armed { get; set; }

        // One-shot button presses: the panel sets these true, PlayerShip
        // consumes and clears them on the next physics frame.
        public bool VectorRequested { get; set; }
        public bool JumpRequested { get; set; }

        // --- Telemetry: written by PlayerShip each physics frame, read by the FTL panel ---
        public FtlPhase Phase { get; private set; } = FtlPhase.Idle;
        public float ChargeProgress { get; private set; } // kept for ControlPanelsWindow UI
        public float JumpProgress { get; private set; }   // kept for ControlPanelsWindow UI
        public float Progress { get; private set; }       // 0→1 charging, 1→0 cooldown, 0 otherwise
        public float SignalLagS { get; private set; }     // signal-lag telemetry (§3.1b batch 8)
        public bool Aborted { get; private set; }         // sticky until the next successful VECTOR

        public void PublishTelemetry(
            FtlPhase phase, float chargeProgress, float jumpProgress,
            float progress, float signalLagS, bool aborted)
        {
            Phase = phase;
            ChargeProgress = chargeProgress;
            JumpProgress = jumpProgress;
            Progress = progress;
            SignalLagS = signalLagS;
            Aborted = aborted;
        }

    }

    // Current touchscreen display mode. Written by OnMqttMessageReceived (MQTT
    // background thread) and read by ControlPanelsWindow._Process (Godot main
    // thread). String reference assignment is atomic in .NET, so no lock needed
    // for this single-writer / single-reader pattern.
    public sealed class TouchscreenState
    {
        // §3.7 default: touchscreen shows Hardpoints status on startup.
        public string Mode { get; set; } = "hardpoints";
    }

    // Active camera view (batch 18). PendingView is written by the MQTT
    // background thread and consumed by CameraController._Process on the main
    // thread, matching the pending-field pattern used throughout the codebase.
    // ActiveView is written by CameraController.SwitchTo (main thread only).
    public sealed class CameraState
    {
        public string ActiveView { get; set; } = "forward";
        // Volatile: written by MQTT background thread, read+cleared by CameraController._Process.
        public volatile string? PendingView;
    }

    // Active alert list. Written by PlayerShip on state transitions, published
    // by PlayerShip and republished by SimBus on each broker reconnect.
    public sealed class AlertsState
    {
        public List<AlertEntry> Active { get; } = new();
    }

    // Per-slot hardpoint state. Written by MQTT input handlers and admin panel;
    // read by publish methods and admin live-mirror.
    public sealed class HardpointSlot
    {
        public string Category { get; set; } = "empty";
        public string? Name    { get; set; }
        public bool Armed      { get; set; }
        public bool Active     { get; set; }
        public float Intensity { get; set; }  // 0-1: intensity / cable length / filter index / range / freq
        public string? Mode    { get; set; }  // "cut" | "weld" | null
        public bool? Attached  { get; set; }  // grapple only; null for other modules

        // --- Cargo/Storage ---
        public float FillPct   { get; set; }         // 0–100
        public string? Contents { get; set; }         // human-readable label, null when empty
        public float? TempC    { get; set; }          // reefer only; null for other cargo
        public float? TempMin  { get; set; }          // reefer only
        public float? TempMax  { get; set; }          // reefer only

        // --- Sensor/EW ---
        public bool ScannerModeActive { get; set; }   // scanner array: true=Active, false=Passive
        public bool ScannerModeBeam   { get; set; }   // scanner array: true=Beam, false=Pulse (Active mode only)
        public float ScannerBearing   { get; set; }   // 0–360, wrapping; encoder_b Y axis
        public bool StealthOn         { get; set; }   // stealth/ECM active state

        // --- Defense ---
        public bool ShieldOn                  { get; set; }
        public string ShieldSelectedFacing    { get; set; } = "fore";  // "fore"|"aft"|"port"|"starboard"
        public Dictionary<string, float> ShieldStrengths { get; set; } = new()
        {
            { "fore", 0.5f }, { "aft", 0.5f }, { "port", 0.5f }, { "starboard", 0.5f },
        };
        public bool PdEngaged          { get; set; }
        public bool MissileLockWarning { get; set; }  // not set by gameplay yet
        public int  DecoyCount         { get; set; } = 12;
    }

    // Immutable alert entry. Id must be stable for the lifetime of a single
    // alert instance -- the touchscreen uses it to avoid re-animating an alert
    // that's already visible. Acknowledged is set only by ack handlers, never
    // by the raise path, and resets to false on every re-raise.
    public sealed record AlertEntry(
        [property: JsonPropertyName("id")]           string Id,
        [property: JsonPropertyName("severity")]     string Severity,
        [property: JsonPropertyName("system")]       string System,
        [property: JsonPropertyName("message")]      string Message,
        [property: JsonPropertyName("timestamp_s")]  long TimestampS,
        [property: JsonPropertyName("acknowledged")] bool Acknowledged = false);

    // ── Admin-override publish methods ────────────────────────────────────────
    // ADMIN OVERRIDE — called by AdminPanelWindow to push state directly to
    // MQTT topics that have no real sim backing yet. Replace each method with
    // real SimBus writes when the corresponding sim logic is added.

    public void PublishAdminOverrideShipLoadout(bool unlocked)
    {
        Mqtt.Publish("coldorbit/output/ship/loadout-unlocked",
            unlocked ? "true" : "false",
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    public void PublishAdminOverrideShipCallsign(string callsign)
    {
        ShipCallsign = callsign;
        Mqtt.Publish("coldorbit/output/ship/callsign", callsign,
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // ADMIN OVERRIDE — publishes a modified propulsion-state payload with the
    // given engine temperature and SOI body. PowerPerEnginekW (1500f) is
    // PlayerShip.PublishMqttTelemetry overwrites this on the next telemetry
    // tick (≤100 ms at TelemetryPublishRateHz = 10).
    public void PublishAdminOverridePropulsionTemp(float tempC, string soiBody)
    {
        var p = Propulsion;
        float enginePowerEach = p.ThrottleInput * p.PowerPerEnginekW;
        int tempClamped = (int)Mathf.Clamp(tempC, 0f, 1000f);
        string payload = JsonSerializer.Serialize(new
        {
            armed = false,
            throttle = MathF.Round(p.ThrottleInput, 3),
            mix = MathF.Round(p.PropellantMix, 3),
            rcs_enabled = p.RcsEnabled,
            dampeners_enabled = p.DampenersEnabled,
            dampener_mode = p.DampenerMode,
            reverse_enabled = p.ReverseEnabled,
            ship_temp_c = tempClamped,
            engines = new object[]
            {
                new { id = "port",      power_kw = (int)enginePowerEach, temp_c = tempClamped },
                new { id = "centre",    power_kw = (int)enginePowerEach, temp_c = tempClamped },
                new { id = "starboard", power_kw = (int)enginePowerEach, temp_c = tempClamped },
            },
            velocity_ms = MathF.Round(p.Velocity, 2),
            acceleration_ms2 = MathF.Round(p.AccelerationMs2, 2),
            altitude_m = MathF.Round(p.AltitudeM, 1),
            soi_body = soiBody,
        });
        Mqtt.Publish("coldorbit/output/propulsion/state", payload,
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    public void PublishAdminOverrideEngineering(
        string systemId, int health, bool disabled,
        string[] effects, int? powerAllocated, int? powerMax)
    {
        // Write to live EngineeringState so ControlPanelsWindow and other
        // in-process readers see the change immediately. disabled=true forces
        // health to 0 (Disabled is derived from Health == 0).
        var sys = Engineering.GetById(systemId);
        sys.Health = disabled ? 0 : health;
        if (powerAllocated.HasValue) sys.PowerAllocatedKW = powerAllocated;

        // Purge from repair queue only if restored to full health.
        // Queue is not auto-populated on damage — that is the player's call.
        if (sys.Health >= 100)
            Engineering.RepairQueue.Remove(systemId);

        // effects[] from the admin panel are ignored — real effects are derived
        // by BuildEffects() from health, so the MQTT payload reflects reality.
        PublishEngineeringState();
        PublishRepairQueue();
    }

    public void PublishAdminOverrideRepairQueue(object[] entries)
    {
        // ADMIN OVERRIDE — replace when real repair logic exists
        Mqtt.Publish("coldorbit/output/repair/queue",
            JsonSerializer.Serialize(entries),
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    public void PublishAdminOverrideCommsLog(object[] messages)
    {
        // ADMIN OVERRIDE — replace when real sim logic exists
        Mqtt.Publish("coldorbit/output/comms/log",
            JsonSerializer.Serialize(messages),
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    public void PublishAdminOverrideCommsTargets(object[] contacts)
    {
        // ADMIN OVERRIDE — replace when real sim logic exists
        Mqtt.Publish("coldorbit/output/comms/targets",
            JsonSerializer.Serialize(contacts),
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    public void PublishAdminOverrideTurret(
        string turret, bool armed, string fireMode, string lockState,
        float? bearingDeg, string? targetName, string? targetClass,
        string? targetAlliance, int? targetRangeM,
        string ammoLoaded, int kineticCount, int empCount, float heat)
    {
        // ADMIN OVERRIDE — replace when real sim logic exists
        string payload = JsonSerializer.Serialize(new
        {
            turret,
            armed,
            fire_mode        = fireMode,
            lock_state       = lockState,
            bearing_deg      = bearingDeg,
            target_name      = targetName,
            target_class     = targetClass,
            target_alliance  = targetAlliance,
            target_range_m   = targetRangeM,
            ammo_loaded      = ammoLoaded,
            ammo_remaining   = new object[]
            {
                new { type = "Kinetic Slug", count = kineticCount },
                new { type = "EMP Round",    count = empCount },
            },
            heat = MathF.Round(heat, 3),
        });
        Mqtt.Publish($"coldorbit/output/turrets/{turret}/state", payload,
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    public void PublishAdminOverrideMissile(
        string tube, bool armed, string status, string? missileType,
        string lockState, string? targetName, string? targetClass,
        string? targetAlliance, int? targetRangeM)
    {
        // ADMIN OVERRIDE — replace when real sim logic exists
        string payload = JsonSerializer.Serialize(new
        {
            tube,
            armed,
            status,
            missile_type     = missileType,
            lock_state       = lockState,
            target_name      = targetName,
            target_class     = targetClass,
            target_alliance  = targetAlliance,
            target_range_m   = targetRangeM,
        });
        Mqtt.Publish($"coldorbit/output/missiles/{tube}/state", payload,
            MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // Fires a HULL IMPACT caution without a physics collision — for testing the
    // alert path when flying into an obstacle is inconvenient. Picked up by
    // PlayerShip.HandleCollision on the next frame so the alert follows the
    // same code path as a real impact (3 s duration, MQTT publish, etc.).
    public void AdminTriggerCollisionAlert()
    {
        Propulsion.PendingAdminCollisionN = Propulsion.CollisionAlertThresholdN + 1f;
    }

    // Simulates atmosphere entry/exit for testing the dampener lockout and
    // DAMPENERS INOP alert without flying to low altitude.
    public void AdminSimulateAtmosphere(bool simulated)
    {
        Propulsion.AdminAtmoSimulated = simulated;
    }

    public void AdminResetToSpawn()
    {
        Propulsion.PendingSpawnReset = true;
    }

    // Admin override for Planet.SurfaceGravity. Deferred to the Godot main
    // thread (_Process) because SurfaceGravity is read on the physics step
    // (PlayerShip._IntegrateForces via GM) and Planet is a scene node — the
    // admin panel shouldn't write it directly from its UI callback.
    // ── Touchscreen mode persistence ──────────────────────────────────────────

    private string LoadTouchscreenMode()
    {
        var cfg = new Godot.ConfigFile();
        if (cfg.Load(PrefsPath) == Godot.Error.Ok)
        {
            string mode = cfg.GetValue("touchscreen", "mode", "hardpoints").AsString();
            return ValidTouchscreenModes.Contains(mode) ? mode : "hardpoints";
        }
        return "hardpoints";
    }

    private void SaveTouchscreenMode(string mode)
    {
        var cfg = new Godot.ConfigFile();
        cfg.Load(PrefsPath); // merge with existing keys
        cfg.SetValue("touchscreen", "mode", mode);
        cfg.Save(PrefsPath);
    }

    public void AdminSetPlanetGravity(float surfaceGravity)
    {
        _pendingPlanetGravity = surfaceGravity;
    }

    // Admin writes base hardpoint fields directly to SimBus.Hardpoints and calls
    // the real publish path. Category-specific fields (cargo/sensor/defense) are
    // written directly to the slot by AdminPanelWindow before calling this method.
    public void AdminUpdateHardpoint(int slot, string category, string? name,
        bool armed, bool active, float intensity, string? mode, bool? attached)
    {
        var hp       = Hardpoints[slot - 1];
        hp.Category  = category;
        hp.Name      = name;
        hp.Armed     = armed;
        hp.Active    = active;
        hp.Intensity = intensity;
        hp.Mode      = mode;
        hp.Attached  = attached;
        PublishHardpointModule(slot);
    }
}

// Master plan §2 / documentation/panel-control-designs.md "FTL" section:
// Arm gates the stack; VECTOR combines destination lock + jump-point clear
// check + spool-up charge (LED flashes orange, solid when Ready); Jump
// executes (LED flashes green, solid when Complete).
//
// Cooldown added in batch 8: after a completed jump or an abort, the drive
// enters Cooldown before returning to Idle. Arm/VECTOR/Jump are no-ops during
// Cooldown. This prevents the drive being used as a panic button (§2).
public enum FtlPhase
{
    Idle,
    Charging,
    Ready,
    Jumping,
    Cooldown,
}
