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
    private void OnMqttConnected()
    {
        Mqtt.Publish(
            "coldorbit/output/touchscreen/mode",
            Touchscreen.Mode,
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true);
    }

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

        // True whenever propulsion is disabled, regardless of cause. Currently
        // only ever set from the overheat cutoff below, but named and read
        // independently of that so a future damage/sabotage system can set it
        // too without any reader (e.g. FTL's jump-abort interrupt) changing.
        public bool IsPropulsionDisabled { get; private set; }

        public void PublishTelemetry(float propellantMix, float engineTemp, bool overheated, bool propulsionDisabled, float velocity)
        {
            PropellantMix = propellantMix;
            EngineTemp = engineTemp;
            Overheated = overheated;
            IsPropulsionDisabled = propulsionDisabled;
            Velocity = velocity;
        }
    }

    public sealed class FtlState
    {
        public static readonly string[] Destinations = { "Sol", "Alpha Centauri", "Wolf 359", "Tau Ceti" };

        // --- Commands: written by the FTL panel, read by PlayerShip ---
        public bool Armed { get; set; }
        public int DestinationIndex { get; set; }

        // One-shot button presses: the panel sets these true, PlayerShip
        // consumes and clears them on the next physics frame.
        public bool VectorRequested { get; set; }
        public bool JumpRequested { get; set; }

        // --- Telemetry: written by PlayerShip each physics frame, read by the FTL panel ---
        public FtlPhase Phase { get; private set; } = FtlPhase.Idle;
        public float ChargeProgress { get; private set; } // 0-1 while Charging
        public float JumpProgress { get; private set; }   // 0-1 while Jumping
        public bool Aborted { get; private set; }          // sticky until the next successful VECTOR

        public void PublishTelemetry(FtlPhase phase, float chargeProgress, float jumpProgress, bool aborted)
        {
            Phase = phase;
            ChargeProgress = chargeProgress;
            JumpProgress = jumpProgress;
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
}

// Master plan §2 / documentation/panel-control-designs.md "FTL" section:
// Arm gates the stack; VECTOR combines destination lock + jump-point clear
// check + spool-up charge (LED flashes orange, solid when Ready); Jump
// executes (LED flashes green, solid when Complete).
public enum FtlPhase
{
    Idle,
    Charging,
    Ready,
    Jumping,
    Complete,
}
