using System.Collections.Generic;
using System.Text.Json;
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
    public MqttTelemetryPublisher Mqtt { get; private set; }

    private static readonly HashSet<string> ValidTouchscreenModes = new()
    {
        "engineering", "propulsion", "ftl", "turrets", "missiles", "comms", "hardpoints",
    };

    public override void _Ready()
    {
        Instance = this;
        Mqtt = new MqttTelemetryPublisher(MqttBrokerHost, MqttBrokerPort);

        // Register subscription and wire events before Start() so the
        // filters and callbacks are in place before the first connect fires.
        Mqtt.Subscribe("coldorbit/input/touchscreen/+");
        Mqtt.MessageReceived += OnMqttMessageReceived;
        Mqtt.Connected += OnMqttConnected;

        Mqtt.Start();
    }

    public override void _ExitTree()
    {
        Mqtt?.Stop();
    }

    // Called on the MQTT background thread whenever a message arrives on any
    // subscribed topic. Routes touchscreen input to mode-select logic; ignores
    // other topics (none registered yet, but safe against future additions).
    private void OnMqttMessageReceived(string topic, string payload)
    {
        const string touchscreenPrefix = "coldorbit/input/touchscreen/";
        if (!topic.StartsWith(touchscreenPrefix, System.StringComparison.Ordinal)) return;

        var mode = topic.Substring(touchscreenPrefix.Length);
        if (!ValidTouchscreenModes.Contains(mode))
        {
            GD.PrintErr($"SimBus: unknown touchscreen mode '{mode}' on topic {topic}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("state", out var stateProp) || stateProp.GetInt32() != 1)
                return; // state:0 (release) is logged but not acted on
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"SimBus: malformed payload on {topic}: {ex.Message}");
            return;
        }

        Touchscreen.Mode = mode;
        Mqtt.Publish(
            "coldorbit/output/touchscreen/mode",
            mode,
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true);
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

        PublishStartupStubs();
        PublishCurrentAlerts();
    }

    // Republishes the current alerts array after a broker reconnect, so
    // a subscriber (the touchscreen) that reconnected after the broker
    // restarted sees the correct alert state immediately.
    internal void PublishCurrentAlerts()
    {
        var payload = JsonSerializer.Serialize(Alerts.Active);
        Mqtt.Publish("coldorbit/output/alerts", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
    }

    // ── Startup stubs ────────────────────────────────────────────────────────
    // MOCK DATA — none of the systems below have real sim logic yet (batch 8).
    // Published on every broker connection so the touchscreen views render
    // something rather than a "waiting" state. Replace each system stub with
    // real telemetry when the corresponding sim logic is added.
    private void PublishStartupStubs()
    {
        PublishEngineeringStubs();
        PublishCommsStubs();
        PublishTurretStubs();
        PublishMissileStubs();
        PublishHardpointStubs();

        // TEMPORARY: always unlocked so the loadout screen is testable
        // without a game-state trigger. Replace with real lock/unlock logic.
        Mqtt.Publish(
            "coldorbit/output/ship/loadout-unlocked",
            "false",
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true);
    }

    private void PublishEngineeringStubs()
    {
        // MOCK — §3.1b engineering contract. Replace with real values when
        // the engineering/damage system exists.
        var systems = new[]
        {
            new { id = "weapons",   hasPower = true  },
            new { id = "engines",   hasPower = true  },
            new { id = "ftl",       hasPower = true  },
            new { id = "reactor",   hasPower = false },
            new { id = "utility_1", hasPower = true  },
            new { id = "utility_2", hasPower = true  },
            new { id = "utility_3", hasPower = true  },
            new { id = "utility_4", hasPower = true  },
            new { id = "hull",      hasPower = false },
        };

        foreach (var sys in systems)
        {
            string payload;
            if (sys.hasPower)
            {
                payload = JsonSerializer.Serialize(new
                {
                    system = sys.id,
                    health = 100,
                    power_allocated = (int?)200,
                    power_unit = (string?)"kW",
                    power_max = (int?)500,
                    disabled = false,
                    repair_queue_position = (int?)null,
                    effects = System.Array.Empty<object>(),
                    repair_eta_seconds = (int?)null,
                });
            }
            else
            {
                payload = JsonSerializer.Serialize(new
                {
                    system = sys.id,
                    health = 100,
                    power_allocated = (int?)null,
                    power_unit = (string?)null,
                    power_max = (int?)null,
                    disabled = false,
                    repair_queue_position = (int?)null,
                    effects = System.Array.Empty<object>(),
                    repair_eta_seconds = (int?)null,
                });
            }

            Mqtt.Publish(
                $"coldorbit/output/engineering/{sys.id}/state",
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
                  text = "Nighthawk, this is Voss. You in position?", timestamp_s = 3600 },
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

    private void PublishHardpointStubs()
    {
        // MOCK — §3.1b hardpoints contract. Replace with real module state when hardpoint system exists.
        for (int slot = 1; slot <= 4; slot++)
        {
            var payload = JsonSerializer.Serialize(new
            {
                slot,
                category = "empty",
                name = (string?)null,
                armed = false,
                updated_at = "2026-08-05T00:00:00Z",
            });
            Mqtt.Publish($"coldorbit/output/hardpoints/{slot}/module", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: true);
        }
    }

    // ── State classes ────────────────────────────────────────────────────────

    public sealed class PropulsionState
    {
        // --- Commands: written by control panels (or keyboard), read by PlayerShip ---
        public bool DampenersEnabled { get; set; } = true;
        public bool RcsEnabled { get; set; } = false;
        public float MixTarget { get; set; } = 0f; // 0 = Economy, 1 = Power

        // --- Telemetry: written by PlayerShip each physics frame, read by control panels ---
        public float PropellantMix { get; private set; }
        public float EngineTemp { get; private set; }
        public bool Overheated { get; private set; }
        public float Velocity { get; private set; }
        public float AccelerationMs2 { get; private set; }
        public float ThrottleInput { get; private set; }  // 0–1, abs of current thrust axis
        public bool ReverseEnabled { get; private set; }  // true while reverse input is active

        // True whenever propulsion is disabled, regardless of cause. Currently
        // only ever set from the overheat cutoff below, but named and read
        // independently of that so a future damage/sabotage system can set it
        // too without any reader (e.g. FTL's jump-abort interrupt) changing.
        public bool IsPropulsionDisabled { get; private set; }

        public void PublishTelemetry(
            float propellantMix, float engineTemp, bool overheated, bool propulsionDisabled,
            float velocity, float accelerationMs2, float throttleInput, bool reverseEnabled)
        {
            PropellantMix = propellantMix;
            EngineTemp = engineTemp;
            Overheated = overheated;
            IsPropulsionDisabled = propulsionDisabled;
            Velocity = velocity;
            AccelerationMs2 = accelerationMs2;
            ThrottleInput = throttleInput;
            ReverseEnabled = reverseEnabled;
        }
    }

    public sealed class FtlState
    {
        public static readonly string[] Destinations =
            { "Sol", "Alpha Centauri", "Wolf 359", "Tau Ceti", "Proxima Centauri" };

        // AU distances matching the destination list above (fiction, for display only).
        public static readonly float[] DestinationRangesAu =
            { 0.5f, 1.4f, 2.8f, 4.1f, 7.2f };

        // --- Commands: written by the FTL panel, read by PlayerShip ---
        public bool Armed { get; set; }
        public int DestinationIndex { get; set; }

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

    // Active alert list. Written by PlayerShip on state transitions, published
    // by PlayerShip and republished by SimBus on each broker reconnect.
    public sealed class AlertsState
    {
        public List<AlertEntry> Active { get; } = new();
    }

    // Immutable alert entry. Id must be stable for the lifetime of a single
    // alert instance -- the touchscreen uses it to avoid re-animating an alert
    // that's already visible.
    public sealed record AlertEntry(
        string Id,
        string Severity,
        string System,
        string Message,
        long TimestampS);
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
