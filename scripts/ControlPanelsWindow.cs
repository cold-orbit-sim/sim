using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using MQTTnet.Protocol;

namespace ColdOrbit.SimCore;

// Basic, functional stand-in for the 12 physical console panels (master
// plan §7), in a separate OS window, before the hardware exists. This is
// about interactive placeholders, not visual fidelity -- it deliberately
// does NOT attempt to match the eventual physical reach-zone layout
// (§3.7); that's deferred until the physical console geometry is settled.
//
// Built programmatically (one method per panel) rather than hand-authored
// node-by-node in the .tscn, since the vast majority of this is repetitive
// control layout across 12 panels, not something worth hand-placing yet.
// The TabContainer organization is itself an interim choice -- expect it
// to get replaced by per-panel windows or a §3.7 layout later.
//
// Every panel except Propulsion is purely visual: interactive (clickable,
// toggles flip, sliders move) but not wired to any sim state, because no
// sim logic exists yet for turrets/missiles/comms/etc. Propulsion is the
// one panel with real state to wire to (PlayerShip via SimBus).
//
// Propulsion and FTL also publish to coldorbit/input/... (batch 6
// follow-up) whenever their controls are actually operated -- standing in
// for what the real physical panels' own MCUs would broadcast once they
// exist. See PublishPropulsionCommand/PublishFtlCommand/PublishFtlAction
// for the topic/retain/QoS reasoning, which mirrors the
// coldorbit/output/... conventions batch 6 established. Deliberately
// fires only from genuine UI interaction (the signal handlers below), not
// from SyncPropulsionFromBus/SyncFtlFromBus's NoSignal syncing -- a real
// panel only broadcasts when its own control moves, not when it hears the
// sim's state changed via a different input path (the keyboard
// placeholder). PlayerShip does not subscribe to these -- still out of
// scope per the batch 6 handover, this is publish-only.
public partial class ControlPanelsWindow : Window
{
    private static readonly Color LedOff = new Color(0.2f, 0.2f, 0.2f);
    private static readonly Color LedGreen = new Color(0.2f, 0.9f, 0.3f);
    private static readonly Color LedOrange = new Color(1.0f, 0.6f, 0.0f);
    private static readonly Color LedRed = new Color(0.9f, 0.15f, 0.15f);

    private ProgressBar _engineTempGauge;
    private Label _overheatLabel;
    private HSlider _mixSlider;
    private CheckButton _rcsToggle;
    private CheckButton _dampenerToggle;

    private static readonly string[] TouchscreenModeNames =
        { "Engineering", "Propulsion", "FTL", "Map", "Turrets", "Missiles", "Comms", "Hardpoints" };

    private Button[] _touchscreenButtons;
    private ColorRect[] _touchscreenLeds;
    private string _lastTouchscreenMode;

    private CheckButton _ftlArmToggle;
    private Button _ftlDestPrev;
    private Button _ftlDestNext;
    private Label _ftlDestLabel;
    private Button _ftlVectorButton;
    private ColorRect _ftlVectorLed;
    private Button _ftlJumpButton;
    private ColorRect _ftlJumpLed;
    private Label _ftlStatusLabel;
    private double _ledBlinkClock; // seconds, drives VECTOR/JUMP LED flash

    private Button _masterWarnButton;
    private Button _masterCautButton;
    private ColorRect _masterWarnLed;
    private ColorRect _masterCautLed;

    // ── Camera panel state ────────────────────────────────────────────────
    private static readonly string[] CameraViewKeys =
        { "forward", "aft", "chase", "dorsal", "ventral", "docking", "damage" };
    private static readonly string[] CameraViewLabels =
        { "Forward", "Aft", "External / Chase", "Dorsal", "Ventral", "Docking", "Damage Inspection" };

    private Button[] _cameraButtons;
    private ColorRect[] _cameraLeds;
    private string _lastCameraView;

    // ── Engineering / repair priority panel state ─────────────────────────
    private static readonly string[] RepairSystemIds =
    {
        "weapons", "engines", "ftl", "reactor",
        "utility_1", "utility_2", "utility_3", "utility_4", "hull",
    };
    private static readonly string[] RepairSystemLabels =
    {
        "Weapons", "Engines", "FTL", "Reactor",
        "Utility 1", "Utility 2", "Utility 3", "Utility 4", "Hull",
    };
    private ColorRect[] _repairStatusLeds;
    private Button[] _repairPriorityBtns;

    // ── Hardpoint panel state (one per slot) ──────────────────────────────
    private sealed class HardpointPanelState
    {
        public int Slot;
        public CheckButton ArmToggle;
        public ColorRect ArmedLed;
        public Label ModuleNameLabel;
        public ColorRect ActiveLed;
        public Label IntensityLabel;
        public Label ModeLabel;
    }
    private readonly List<HardpointPanelState> _hardpointPanels = new();

    // ── Turret panel state (one per turret) ───────────────────────────────
    private sealed class TurretPanelState
    {
        public string TurretId;
        public CheckButton ArmToggle;
        public Label TargetLabel;
        public ColorRect LockLed;
        public Label LockLabel;
        public Label BearingLabel;
        public Label ElevationLabel;
        public Label RangeLabel;
        public ProgressBar AmmoGauge;
        public Label AmmoLabel;
        public ProgressBar HeatGauge;
        public Button ReloadButton;
        public ColorRect ReloadLed;
    }
    private readonly List<TurretPanelState> _turretPanels = new();

    public override void _Ready()
    {
        Title = "Cold Orbit — Control Panels";
        var rect = WindowLayout.ControlPanelsRect();
        Position = rect.Position;
        Size = rect.Size;

        var tabs = new TabContainer();
        tabs.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(tabs);

        AddTab(tabs, "Turrets", BuildTurretsTab());
        AddTab(tabs, "Missiles", BuildMissilesTab());
        AddTab(tabs, "Cameras", BuildCamerasTab());
        AddTab(tabs, "Touchscreen", BuildTouchscreenModeTab());
        AddTab(tabs, "Comms", BuildCommsTab());
        AddTab(tabs, "FTL", BuildFtlTab());
        AddTab(tabs, "Propulsion", BuildPropulsionTab());
        AddTab(tabs, "Engineering", BuildEngineeringTab());
        for (int i = 1; i <= 4; i++)
        {
            AddTab(tabs, $"Hardpoint {i}", BuildHardpointTab(i));
        }

        Show();
    }

    public override void _Process(double delta)
    {
        SyncPropulsionFromBus();
        SyncFtlFromBus(delta);
        SyncTouchscreenFromBus();
        SyncCommsFromBus();
        SyncHardpointsFromBus();
        SyncCamerasFromBus();
        SyncRepairFromBus();
        SyncTurretsFromBus();
    }

    // --- Layout helpers -----------------------------------------------

    private static void AddTab(TabContainer tabs, string title, Control content)
    {
        var margin = new MarginContainer { Name = title };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);

        var scroll = new ScrollContainer();
        margin.AddChild(scroll);
        scroll.AddChild(content);

        tabs.AddChild(margin);
    }

    private static HBoxContainer Row(params Control[] children)
    {
        var row = new HBoxContainer();
        foreach (var c in children) row.AddChild(c);
        return row;
    }

    private static Control Labeled(string text, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = text, CustomMinimumSize = new Vector2(180, 0) });
        row.AddChild(control);
        return row;
    }

    private static ColorRect MakeLed(Color color)
    {
        return new ColorRect { Color = color, CustomMinimumSize = new Vector2(20, 20) };
    }

    private static ProgressBar MakeGauge(float initial = 100f)
    {
        return new ProgressBar { MinValue = 0, MaxValue = 100, Value = initial, CustomMinimumSize = new Vector2(150, 0) };
    }

    private static HSlider MakeKnob(double min = 0, double max = 100, double value = 50)
    {
        return new HSlider { MinValue = min, MaxValue = max, Value = value, CustomMinimumSize = new Vector2(150, 0) };
    }

    private static Control MakeCycleSelect(string[] options)
    {
        var container = new HBoxContainer();
        var index = 0;
        var prev = new Button { Text = "◀" };
        var label = new Label
        {
            Text = options[0],
            CustomMinimumSize = new Vector2(120, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var next = new Button { Text = "▶" };

        prev.Pressed += () =>
        {
            index = (index - 1 + options.Length) % options.Length;
            label.Text = options[index];
        };
        next.Pressed += () =>
        {
            index = (index + 1) % options.Length;
            label.Text = options[index];
        };

        container.AddChild(prev);
        container.AddChild(label);
        container.AddChild(next);
        return container;
    }

    // --- Panel builders -------------------------------------------------
    // No backing sim logic yet, except BuildPropulsionTab.

    private Control BuildTurretsTab()
    {
        var root = new VBoxContainer();
        _turretPanels.Clear();

        foreach (var turretId in new[] { "dorsal", "ventral" })
        {
            var state = SimBus.Instance.GetTurret(turretId);
            var p = new TurretPanelState { TurretId = turretId };

            root.AddChild(new Label { Text = $"── {turretId} turret ──" });

            p.ArmToggle = new CheckButton();
            // Arming is the gate on tracking: TurretController drops its target
            // and stops traversing while unarmed (see TurretController._Process).
            p.ArmToggle.Toggled += pressed =>
            {
                state.Armed = pressed;
                PublishButtonStateQos1($"coldorbit/input/turrets/{turretId}/arm", pressed ? 1 : 0);
            };
            root.AddChild(Labeled("Arm", p.ArmToggle));

            // Target select posts a one-shot cycle request; the actual selection
            // happens on the main thread in ShipMesh, and the labels below follow
            // from telemetry — no local mutation here, same reasoning as the FTL
            // destination select.
            var prev = new Button { Text = "◀" };
            var next = new Button { Text = "▶" };
            prev.Pressed += () => state.PendingTargetCycle = -1;
            next.Pressed += () => state.PendingTargetCycle = 1;
            p.TargetLabel = new Label
            {
                Text = "-- none --",
                CustomMinimumSize = new Vector2(140, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            root.AddChild(Labeled("Target Select", Row(prev, p.TargetLabel, next)));

            p.LockLed = MakeLed(LedOff);
            p.LockLabel = new Label { Text = "none" };
            root.AddChild(Labeled("Lock", Row(p.LockLed, p.LockLabel)));

            p.BearingLabel   = new Label { Text = "---" };
            p.ElevationLabel = new Label { Text = "---" };
            p.RangeLabel     = new Label { Text = "---" };
            root.AddChild(Labeled("Bearing", p.BearingLabel));
            root.AddChild(Labeled("Elevation", p.ElevationLabel));
            root.AddChild(Labeled("Range", p.RangeLabel));

            // Ammo / heat / reload wired to real TurretController state (batch 26 —
            // previously cosmetic placeholders left over from batch 24).
            p.AmmoGauge = MakeGauge();
            p.AmmoLabel = new Label { Text = "---" };
            root.AddChild(Labeled("Ammo", Row(p.AmmoGauge, p.AmmoLabel)));

            p.HeatGauge = MakeGauge(0f);
            root.AddChild(Labeled("Heat", p.HeatGauge));

            p.ReloadButton = new Button { Text = "Reload" };
            p.ReloadLed = MakeLed(LedOff);
            p.ReloadButton.Pressed += () => { state.PendingReloadRequest = true; };
            root.AddChild(Labeled("Reload", Row(p.ReloadButton, p.ReloadLed)));

            root.AddChild(new HSeparator());
            _turretPanels.Add(p);
        }

        root.AddChild(Labeled("Fire Mode (Single/Burst)", new CheckButton()));
        return root;
    }

    private void SyncTurretsFromBus()
    {
        foreach (var p in _turretPanels)
        {
            var state = SimBus.Instance.GetTurret(p.TurretId);
            if (state == null) continue;

            // Live-mirror write: NoSignal so a future non-panel arm path (MQTT,
            // keyboard) doesn't re-fire Toggled and loop back out as input spam.
            p.ArmToggle.SetPressedNoSignal(state.Armed);

            p.TargetLabel.Text = state.TargetName ?? "-- none --";

            (p.LockLed.Color, p.LockLabel.Text) = state.LockState switch
            {
                TurretLockState.Locked    => (LedGreen,  "LOCKED"),
                TurretLockState.Acquiring => (LedOrange, $"acquiring… {state.LockProgress * 100f:F0}%"),
                _                         => (LedOff,    "none"),
            };

            p.BearingLabel.Text   = state.BearingDeg.HasValue   ? $"{state.BearingDeg.Value:F1}°" : "---";
            p.ElevationLabel.Text = state.ElevationDeg.HasValue ? $"{state.ElevationDeg.Value:F1}°" : "---";
            p.RangeLabel.Text     = state.TargetRangeM.HasValue  ? $"{state.TargetRangeM.Value} m" : "---";

            int remaining = state.AmmoRemaining.GetValueOrDefault(state.AmmoLoaded, 0);
            int max = state.AmmoMaxCapacity.GetValueOrDefault(state.AmmoLoaded, remaining);
            p.AmmoGauge.Value = max > 0 ? (remaining / (float)max) * 100f : 0f;
            p.AmmoLabel.Text = $"{remaining}/{max} {state.AmmoLoaded}";

            p.HeatGauge.Value = state.Heat * 100f;
            p.ReloadLed.Color = state.Reloading ? LedOrange : LedOff;
            p.ReloadButton.Disabled = state.Reloading || remaining >= max;
        }
    }

    private Control BuildMissilesTab()
    {
        var root = new VBoxContainer();
        for (int i = 1; i <= 2; i++)
        {
            root.AddChild(new Label { Text = $"Missile Bay {i}" });
            root.AddChild(Labeled("Arm", new CheckButton()));
            // "4-position select" -- ammo type names are placeholders, not
            // from the master plan (it specifies the control is 4-position,
            // not what the 4 options are).
            root.AddChild(Labeled("Ammo Type", MakeCycleSelect(new[] { "HE", "AP", "Flak", "EMP" })));
            root.AddChild(Labeled("Load", new Button { Text = "Load" }));
            root.AddChild(Labeled("Target Select", MakeCycleSelect(new[] { "None", "Tgt A", "Tgt B", "Tgt C" })));
            root.AddChild(Labeled("Lock", MakeLed(LedOff)));
            root.AddChild(Labeled("Fire", new Button { Text = "Fire" }));
            root.AddChild(new HSeparator());
        }
        return root;
    }

    private Control BuildCamerasTab()
    {
        var root = new VBoxContainer();
        var group = new ButtonGroup();
        _cameraButtons = new Button[CameraViewKeys.Length];
        _cameraLeds = new ColorRect[CameraViewKeys.Length];

        for (int i = 0; i < CameraViewKeys.Length; i++)
        {
            var viewKey = CameraViewKeys[i];
            var topic = $"coldorbit/input/cameras/{viewKey}";

            var led = MakeLed(LedOff);
            var button = new Button { Text = CameraViewLabels[i], ToggleMode = true, ButtonGroup = group };
            button.ButtonDown += () => PublishButtonStateQos1(topic, 1);
            button.ButtonUp += () => PublishButtonStateQos1(topic, 0);

            _cameraButtons[i] = button;
            _cameraLeds[i] = led;
            root.AddChild(Row(button, led));
        }
        return root;
    }

    private void SyncCamerasFromBus()
    {
        if (_cameraButtons == null) return;
        string view = SimBus.Instance?.Cameras?.ActiveView ?? "forward";
        if (view == _lastCameraView) return;
        _lastCameraView = view;

        for (int i = 0; i < CameraViewKeys.Length; i++)
        {
            bool active = CameraViewKeys[i] == view;
            _cameraButtons[i].SetPressedNoSignal(active);
            _cameraLeds[i].Color = active ? LedGreen : LedOff;
        }
    }

    private Control BuildTouchscreenModeTab()
    {
        var root = new VBoxContainer();
        var group = new ButtonGroup();
        _touchscreenButtons = new Button[TouchscreenModeNames.Length];
        _touchscreenLeds = new ColorRect[TouchscreenModeNames.Length];

        for (int i = 0; i < TouchscreenModeNames.Length; i++)
        {
            var modeKey = TouchscreenModeNames[i].ToLowerInvariant();
            var topic = $"coldorbit/input/touchscreen/{modeKey}";

            var led = MakeLed(LedOff);
            var button = new Button { Text = TouchscreenModeNames[i], ToggleMode = true, ButtonGroup = group };
            button.ButtonDown += () => PublishButtonState(topic, 1);
            button.ButtonUp += () => PublishButtonState(topic, 0);

            _touchscreenButtons[i] = button;
            _touchscreenLeds[i] = led;
            root.AddChild(Row(button, led));
        }
        return root;
    }

    private Control BuildCommsTab()
    {
        var root = new VBoxContainer();

        // Master Warn: acks all warnings AND cautions (higher-severity button — §3.1b).
        // LED lit (red) while any warning is unacknowledged.
        _masterWarnLed = MakeLed(LedOff);
        _masterWarnButton = new Button { Text = "Master Warn" };
        _masterWarnButton.ButtonDown += () => PublishButtonStateQos1("coldorbit/input/comms/master_warn", 1);
        _masterWarnButton.ButtonUp   += () => PublishButtonStateQos1("coldorbit/input/comms/master_warn", 0);
        root.AddChild(Row(_masterWarnButton, _masterWarnLed));

        // Master Caution: acks cautions only.
        // LED lit (orange) while any caution is unacknowledged.
        _masterCautLed = MakeLed(LedOff);
        _masterCautButton = new Button { Text = "Master Caution" };
        _masterCautButton.ButtonDown += () => PublishButtonStateQos1("coldorbit/input/comms/master_caut", 1);
        _masterCautButton.ButtonUp   += () => PublishButtonStateQos1("coldorbit/input/comms/master_caut", 0);
        root.AddChild(Row(_masterCautButton, _masterCautLed));

        root.AddChild(Labeled("Volume", MakeKnob(0, 100, 50)));
        root.AddChild(Labeled("Clock", new Label { Text = "00:00:00" }));
        root.AddChild(Labeled("Comms LCD", new Label
        {
            Text = "-- no message --",
            CustomMinimumSize = new Vector2(300, 60),
        }));
        root.AddChild(Labeled("New Message", MakeLed(LedOff)));
        root.AddChild(Row(new Button { Text = "Up" }, new Button { Text = "Down" }, new Button { Text = "Select" }));
        return root;
    }

    private Control BuildFtlTab()
    {
        var root = new VBoxContainer();

        _ftlArmToggle = new CheckButton { Text = "Arm" };
        _ftlArmToggle.Toggled += pressed =>
        {
            SimBus.Instance.Ftl.Armed = pressed;
            PublishFtlCommand();
        };
        root.AddChild(_ftlArmToggle);

        _ftlDestLabel = new Label
        {
            Text = SimBus.Instance.Ftl.SelectedDisplayName,
            CustomMinimumSize = new Vector2(120, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _ftlDestPrev = new Button { Text = "◀" };
        _ftlDestNext = new Button { Text = "▶" };
        // Publish the action only; SimBus.HandleFtlCommand is the single
        // authority that cycles the selection and republishes target/system.
        // The label follows via SyncFtlFromBus, so no local mutation here (a
        // local CycleDestination would double-step once the message loops back).
        _ftlDestPrev.Pressed += () => PublishFtlDestAction("prev");
        _ftlDestNext.Pressed += () => PublishFtlDestAction("next");
        root.AddChild(Labeled("Destination Select", Row(_ftlDestPrev, _ftlDestLabel, _ftlDestNext)));

        // VECTOR/JUMP are momentary presses (not toggles) -- each one sets a
        // one-shot request flag on SimBus.Ftl that PlayerShip consumes on the
        // next physics frame. LED color/blink is driven entirely by the FTL
        // phase telemetry in SyncFtlFromBus, not by the button's own state.
        _ftlVectorLed = MakeLed(LedOff);
        _ftlVectorButton = new Button { Text = "VECTOR" };
        _ftlVectorButton.ButtonDown += () =>
        {
            SimBus.Instance.Ftl.VectorRequested = true;
            PublishButtonState("coldorbit/input/ftl/vector", 1);
        };
        _ftlVectorButton.ButtonUp += () => PublishButtonState("coldorbit/input/ftl/vector", 0);
        root.AddChild(Row(_ftlVectorButton, _ftlVectorLed));

        _ftlJumpLed = MakeLed(LedOff);
        _ftlJumpButton = new Button { Text = "JUMP" };
        _ftlJumpButton.ButtonDown += () =>
        {
            SimBus.Instance.Ftl.JumpRequested = true;
            PublishButtonState("coldorbit/input/ftl/jump", 1);
        };
        _ftlJumpButton.ButtonUp += () => PublishButtonState("coldorbit/input/ftl/jump", 0);
        root.AddChild(Row(_ftlJumpButton, _ftlJumpLed));

        _ftlStatusLabel = new Label { Text = "" };
        root.AddChild(_ftlStatusLabel);

        return root;
    }



    // --- coldorbit/input/... publishing (stand-in for real panel MCUs) --

    // Discrete panel positions (mix knob, RCS/dampener toggles): retained +
    // QoS 2 -- a subscriber connecting fresh sees the current switch position
    // immediately, and exactly-once delivery means no duplicate state flips.
    private static void PublishPropulsionCommand()
    {
        if (SimBus.Instance?.Mqtt == null) return;
        var p = SimBus.Instance.Propulsion;
        string payload = JsonSerializer.Serialize(new
        {
            mix = p.MixTarget,
            rcs_enabled = p.RcsEnabled,
            dampeners_enabled = p.DampenersEnabled,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        SimBus.Instance.Mqtt.Publish("coldorbit/input/propulsion/command", payload, MqttQualityOfServiceLevel.ExactlyOnce, retain: true);
    }

    private static void PublishFtlCommand()
    {
        if (SimBus.Instance?.Mqtt == null) return;
        var ftl = SimBus.Instance.Ftl;
        string payload = JsonSerializer.Serialize(new
        {
            armed = ftl.Armed,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        SimBus.Instance.Mqtt.Publish("coldorbit/input/ftl/command", payload, MqttQualityOfServiceLevel.ExactlyOnce, retain: true);
    }

    // Destination navigation is a momentary prev/next action on the physical
    // panel, not a retained absolute index -- publish it as a fire-and-forget
    // event mirroring the two nav buttons. Shares the ftl/command topic with
    // the arm toggle, but is NOT retained: replaying a stale "next" on
    // reconnect would silently walk the selection.
    private static void PublishFtlDestAction(string action)
    {
        if (SimBus.Instance?.Mqtt == null) return;
        string payload = JsonSerializer.Serialize(new
        {
            dest_action = action,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        SimBus.Instance.Mqtt.Publish("coldorbit/input/ftl/command", payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: false);
    }

    // Momentary button press/release: per-topic (topic is the event, not a
    // payload field), QoS 2 for uniformity and to prevent duplicate deliveries
    // on stateful inputs (e.g. cycle selects). NOT retained -- a stale
    // "button held" on the broker after the publisher exits would be wrong.
    private static void PublishButtonState(string topic, int state)
    {
        if (SimBus.Instance?.Mqtt == null) return;
        string payload = JsonSerializer.Serialize(new
        {
            state,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        SimBus.Instance.Mqtt.Publish(topic, payload, MqttQualityOfServiceLevel.ExactlyOnce, retain: false);
    }

    // QoS 1 variant for alert-ack topics (spec §3.1b batch 10).
    private static void PublishButtonStateQos1(string topic, int state)
    {
        if (SimBus.Instance?.Mqtt == null) return;
        string payload = JsonSerializer.Serialize(new
        {
            state,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        SimBus.Instance.Mqtt.Publish(topic, payload, MqttQualityOfServiceLevel.AtLeastOnce, retain: false);
    }

    private Control BuildPropulsionTab()
    {
        var root = new VBoxContainer();

        // Arm / Ignition / Reverse Thrust / Emergency Cutoff have no
        // distinct backing state in PlayerShip.cs -- there's no separate
        // armed gate, ignition sequence, reverse-thrust mode distinct from
        // holding S, or cutoff distinct from the overheat cutoff. Visual
        // only; flagged in HANDOVER-BACK.md.
        root.AddChild(Labeled("Arm", new CheckButton()));
        root.AddChild(new Button { Text = "Ignition / Start" });

        _mixSlider = MakeKnob(0, 1, 0);
        _mixSlider.Step = 0.01;
        _mixSlider.ValueChanged += v =>
        {
            SimBus.Instance.Propulsion.MixTarget = (float)v;
            PublishPropulsionCommand();
        };
        root.AddChild(Labeled("Propellant Mix (Economy <-> Power)", _mixSlider));

        _engineTempGauge = MakeGauge(0);
        _engineTempGauge.MaxValue = 1000; // matches PlayerShip's engine-temp clamp range
        root.AddChild(Labeled("Engine Temp", _engineTempGauge));

        _overheatLabel = new Label { Text = "" };
        root.AddChild(_overheatLabel);

        _rcsToggle = new CheckButton { Text = "RCS" };
        _rcsToggle.Toggled += pressed =>
        {
            SimBus.Instance.Propulsion.RcsEnabled = pressed;
            PublishPropulsionCommand();
        };
        root.AddChild(_rcsToggle);

        root.AddChild(new CheckButton { Text = "Reverse Thrust" }); // visual only, see comment above
        root.AddChild(new Button { Text = "Emergency Cutoff" });    // visual only, see comment above

        _dampenerToggle = new CheckButton { Text = "Dampeners", ButtonPressed = true };
        _dampenerToggle.Toggled += pressed =>
        {
            SimBus.Instance.Propulsion.DampenersEnabled = pressed;
            PublishPropulsionCommand();
        };
        root.AddChild(_dampenerToggle);

        return root;
    }

    private Control BuildEngineeringTab()
    {
        var root = new VBoxContainer();
        string[] encoderNames = { "Weapons", "Engines", "FTL", "Utility 1", "Utility 2", "Utility 3", "Utility 4" };
        foreach (var name in encoderNames)
        {
            var led = MakeLed(LedOff);
            var knob = MakeKnob();
            root.AddChild(Labeled(name, Row(knob, led)));
        }

        root.AddChild(new HSeparator());
        root.AddChild(Labeled("Reactor Output", MakeKnob(0, 100, 100)));
        root.AddChild(Labeled("Total Power", MakeGauge(100)));
        root.AddChild(new Button { Text = "SCRAM" }); // hold-to-confirm deliberately skipped this batch

        root.AddChild(new HSeparator());
        root.AddChild(new Label { Text = "── Repair Priority ──" });

        int n = RepairSystemIds.Length;
        _repairStatusLeds   = new ColorRect[n];
        _repairPriorityBtns = new Button[n];

        for (int idx = 0; idx < n; idx++)
        {
            int captured = idx; // capture for closure
            _repairStatusLeds[idx] = MakeLed(LedOff);
            _repairPriorityBtns[idx] = new Button
            {
                Text = "Prioritize",
                Disabled = true,
                CustomMinimumSize = new Vector2(90, 0),
            };
            _repairPriorityBtns[idx].Pressed += () => OnRepairPrioritize(captured);

            root.AddChild(Row(
                new Label { Text = RepairSystemLabels[captured], CustomMinimumSize = new Vector2(80, 0) },
                _repairStatusLeds[captured],
                _repairPriorityBtns[captured]));
        }

        SyncRepairFromBus();
        return root;
    }

    // Moves the selected subsystem to the front of the repair queue (or adds
    // it if not already queued). Does nothing for healthy systems. Publishes
    // the updated queue immediately.
    private void OnRepairPrioritize(int idx)
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;
        string sysId = RepairSystemIds[idx];
        var sys = eng.GetById(sysId);
        if (sys.Health >= 100) return;

        eng.RepairQueue.Remove(sysId);
        eng.RepairQueue.Insert(0, sysId);
        SimBus.Instance.PublishRepairQueue();
        SyncRepairFromBus();
    }

    // Updates repair status LEDs and button states from live Engineering.
    // LED colours: off = healthy, red = damaged/not queued, orange = queued, green = repairing.
    private void SyncRepairFromBus()
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;

        for (int idx = 0; idx < RepairSystemIds.Length; idx++)
        {
            var sys = eng.GetById(RepairSystemIds[idx]);
            int queuePos = eng.RepairQueue.IndexOf(sys.Id);

            if (sys.Health >= 100)
            {
                _repairStatusLeds[idx].Color = LedOff;
                _repairPriorityBtns[idx].Disabled = true;
            }
            else if (queuePos == 0)
            {
                _repairStatusLeds[idx].Color = LedGreen;  // actively repairing
                _repairPriorityBtns[idx].Disabled = true; // already first
            }
            else if (queuePos > 0)
            {
                _repairStatusLeds[idx].Color = LedOrange; // queued, waiting
                _repairPriorityBtns[idx].Disabled = false;
            }
            else
            {
                _repairStatusLeds[idx].Color = LedRed;    // damaged, not queued
                _repairPriorityBtns[idx].Disabled = false;
            }
        }
    }

    private Control BuildHardpointTab(int slot)
    {
        var hp = new HardpointPanelState { Slot = slot };
        var root = new VBoxContainer();

        // ── Arm ──────────────────────────────────────────────────────────
        hp.ArmedLed = MakeLed(LedOff);
        hp.ArmToggle = new CheckButton { Text = "Arm" };
        hp.ArmToggle.Toggled += pressed =>
        {
            int state = pressed ? 1 : 0;
            PublishHardpointInput(slot, "arm",
                $"{{\"state\":{state},\"updated_at\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        };
        root.AddChild(Row(hp.ArmToggle, hp.ArmedLed));

        // ── Module display ────────────────────────────────────────────────
        hp.ModuleNameLabel = new Label
        {
            Text = "—",
            CustomMinimumSize = new Vector2(220, 0),
        };
        hp.ActiveLed = MakeLed(LedOff);
        root.AddChild(Row(hp.ModuleNameLabel, hp.ActiveLed));

        hp.IntensityLabel = new Label { Text = "0%" };
        root.AddChild(Labeled("Intensity / Cable", hp.IntensityLabel));

        hp.ModeLabel = new Label { Text = "—" };
        root.AddChild(Labeled("Mode", hp.ModeLabel));

        // ── Soft keys ─────────────────────────────────────────────────────
        // Layout matches §7.9 soft-key table: SK1-4 row, SK5-8 row.
        // SK5=ON/LAUNCH, SK6=OFF/RELEASE, SK3=WELD, SK7=CUT, SK1/2/4/8=aim.
        root.AddChild(new Label { Text = "Soft keys:" });
        var (labels, keys) = (
            new[] { "◄ SK1", "▲ SK2", "WELD SK3", "▼ SK4", "ON SK5", "OFF SK6", "CUT SK7", "► SK8" },
            new[] { "SK1",   "SK2",   "SK3",       "SK4",   "SK5",    "SK6",     "SK7",     "SK8"   }
        );
        var grid = new GridContainer { Columns = 4 };
        for (int i = 0; i < 8; i++)
        {
            var btn = new Button { Text = labels[i], CustomMinimumSize = new Vector2(90, 0) };
            var key = keys[i];
            var s = slot;
            btn.ButtonDown += () => PublishHardpointInput(s, "softkey",
                $"{{\"key\":\"{key}\",\"state\":1,\"updated_at\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
            btn.ButtonUp   += () => PublishHardpointInput(s, "softkey",
                $"{{\"key\":\"{key}\",\"state\":0,\"updated_at\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
            grid.AddChild(btn);
        }
        root.AddChild(grid);

        // ── Encoder A (intensity / cable length) ─────────────────────────
        // Real encoder sends +1/-1 per detent; buttons model that directly.
        root.AddChild(new Label { Text = "Encoder A (intensity / cable):" });
        var encDec = new Button { Text = "−" };
        var encInc = new Button { Text = "+" };
        encDec.Pressed += () => PublishHardpointInput(slot, "encoder_a",
            $"{{\"delta\":-1,\"updated_at\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        encInc.Pressed += () => PublishHardpointInput(slot, "encoder_a",
            $"{{\"delta\":1,\"updated_at\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        root.AddChild(Row(encDec, encInc));

        _hardpointPanels.Add(hp);
        return root;
    }

    private static void PublishHardpointInput(int slot, string subTopic, string payload)
    {
        SimBus.Instance?.Mqtt?.Publish(
            $"coldorbit/input/hardpoints/{slot}/{subTopic}",
            payload,
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: false);
    }

    // --- Hardpoints <-> SimBus sync --------------------------------------

    private void SyncHardpointsFromBus()
    {
        if (SimBus.Instance == null) return;
        foreach (var hp in _hardpointPanels)
        {
            var s = SimBus.Instance.Hardpoints[hp.Slot - 1];
            hp.ArmToggle.SetPressedNoSignal(s.Armed);
            hp.ArmedLed.Color = s.Armed ? LedGreen : LedOff;
            hp.ActiveLed.Color = s.Active ? LedGreen : LedOff;

            string name = s.Category == "empty" ? "(empty)" : (s.Name ?? s.Category);
            if (hp.ModuleNameLabel.Text != name) hp.ModuleNameLabel.Text = name;

            string intensity = $"{s.Intensity * 100f:0}%";
            if (hp.IntensityLabel.Text != intensity) hp.IntensityLabel.Text = intensity;

            string mode = s.Mode ?? "—";
            if (hp.ModeLabel.Text != mode) hp.ModeLabel.Text = mode;
        }
    }

    // --- Touchscreen <-> SimBus sync -------------------------------------

    // LED and button state are driven entirely by the MQTT round-trip
    // (press → input topic → sim-core → output topic → SimBus.Touchscreen.Mode)
    // rather than the button's own toggle state, so a future sim-core override
    // (e.g. mode locked during loadout) reflects in the UI without extra wiring.
    private void SyncTouchscreenFromBus()
    {
        if (SimBus.Instance == null || _touchscreenButtons == null) return;

        var mode = SimBus.Instance.Touchscreen.Mode;
        if (mode == _lastTouchscreenMode) return;
        _lastTouchscreenMode = mode;

        for (int i = 0; i < _touchscreenButtons.Length; i++)
        {
            bool active = TouchscreenModeNames[i].ToLowerInvariant() == mode;
            if (_touchscreenButtons[i].ButtonPressed != active)
                _touchscreenButtons[i].SetPressedNoSignal(active);
            _touchscreenLeds[i].Color = active ? LedGreen : LedOff;
        }
    }

    // --- Comms <-> SimBus sync -------------------------------------------

    // Master Warn / Master Caution LEDs reflect unacknowledged alert state.
    // SetPressedNoSignal is not needed here -- LEDs are ColorRects, not buttons.
    private void SyncCommsFromBus()
    {
        if (SimBus.Instance == null || _masterWarnLed == null) return;
        var alerts = SimBus.Instance.Alerts;
        bool warnUnacked = alerts.Active.Exists(a => a.Severity == "warning" && !a.Acknowledged);
        bool cautUnacked = alerts.Active.Exists(a => a.Severity == "caution" && !a.Acknowledged);
        _masterWarnLed.Color = warnUnacked ? LedRed    : LedOff;
        _masterCautLed.Color = cautUnacked ? LedOrange : LedOff;
    }

    // --- Propulsion <-> SimBus sync --------------------------------------

    private void SyncPropulsionFromBus()
    {
        if (SimBus.Instance == null) return;
        var propulsion = SimBus.Instance.Propulsion;

        _engineTempGauge.Value = propulsion.EngineTemp;
        _overheatLabel.Text = propulsion.Overheated
            ? "OVERHEAT -- propulsion disabled"
            : "";

        // Use SetPressedNoSignal / SetValueNoSignal so a keyboard-driven
        // change (X, V, 1, 2) reflects in the UI without re-firing the
        // Toggled/ValueChanged handlers back at SimBus.
        if (_rcsToggle.ButtonPressed != propulsion.RcsEnabled)
        {
            _rcsToggle.SetPressedNoSignal(propulsion.RcsEnabled);
        }

        if (_dampenerToggle.ButtonPressed != propulsion.DampenersEnabled)
        {
            _dampenerToggle.SetPressedNoSignal(propulsion.DampenersEnabled);
        }

        if (!Mathf.IsEqualApprox((float)_mixSlider.Value, propulsion.MixTarget))
        {
            _mixSlider.SetValueNoSignal(propulsion.MixTarget);
        }
    }

    // --- FTL <-> SimBus sync ---------------------------------------------

    private void SyncFtlFromBus(double delta)
    {
        if (SimBus.Instance == null) return;
        var ftl = SimBus.Instance.Ftl;

        if (_ftlArmToggle.ButtonPressed != ftl.Armed)
        {
            _ftlArmToggle.SetPressedNoSignal(ftl.Armed);
        }

        // Destination can only be (re-)selected while idle -- once VECTOR
        // locks it in, prev/next stop having an effect (panel-control-designs.md:
        // "VECTOR is one action combining destination lock...").
        bool destLocked = ftl.Phase is not FtlPhase.Idle;
        _ftlDestPrev.Disabled = destLocked;
        _ftlDestNext.Disabled = destLocked;
        _ftlDestLabel.Text = ftl.SelectedDisplayName;

        _ledBlinkClock += delta;
        bool blinkOn = (_ledBlinkClock % 0.5) < 0.25;

        _ftlVectorLed.Color = ftl.Phase switch
        {
            FtlPhase.Charging => blinkOn ? LedOrange : LedOff,
            FtlPhase.Ready or FtlPhase.Jumping => LedOrange,
            _ => LedOff,
        };

        _ftlJumpLed.Color = ftl.Phase switch
        {
            FtlPhase.Jumping => blinkOn ? LedGreen : LedOff,
            FtlPhase.Cooldown => LedGreen,
            _ => LedOff,
        };

        _ftlVectorButton.Disabled = !ftl.Armed || ftl.Phase != FtlPhase.Idle;
        _ftlJumpButton.Disabled = ftl.Phase != FtlPhase.Ready;

        _ftlStatusLabel.Text = ftl.Phase switch
        {
            FtlPhase.Idle => ftl.Aborted ? "ABORTED -- propulsion disabled" : "",
            FtlPhase.Charging => $"Charging... {ftl.ChargeProgress * 100f:0}%",
            FtlPhase.Ready => "READY",
            FtlPhase.Jumping => $"Jumping... {ftl.JumpProgress * 100f:0}%",
            FtlPhase.Cooldown => "COOLDOWN",
            _ => "",
        };
    }
}
