// AdminPanelWindow.cs
// MAINTENANCE: when a new system is wired in sim-core, add controls here:
//   1. Add a BuildXxxTab() method
//   2. Add a SyncXxxFromBus() call in _Process
//   3. Replace any PublishAdminOverride* stub with real SimBus writes
//   4. Update the handover back doc so the next batch knows what's covered

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ColdOrbit.SimCore;

public partial class AdminPanelWindow : Window
{
    // ── Mirror-active flag ────────────────────────────────────────────────────
    // Set true immediately before OptionButton.Select() or LineEdit.Text = ""
    // during live-mirror updates, cleared immediately after. Handlers that write
    // back to SimBus check this and return early, preventing the sim→UI→sim loop.
    //
    // CheckButton uses SetPressedNoSignal (no flag needed).
    // HSlider and SpinBox use SetValueNoSignal (no flag needed).
    private bool _mirrorActive;

    // ── Propulsion controls ───────────────────────────────────────────────────
    private CheckButton _propArmToggle;
    private HSlider _propThrottleSlider;
    private HSlider _propMixSlider;
    private CheckButton _propRcsToggle;
    private CheckButton _propDampenerToggle;
    private Label _propDampenerModeLabel;
    private CheckButton _propReverseToggle;
    private HSlider _propEngineTempSlider;
    private HSlider _propThrustMultSlider;
    private CheckButton _propOvertempBypassToggle;
    private Label _propVelocityLabel;
    private Label _propAltitudeLabel;
    private Label _propCollisionForceLabel;
    private Label _propSoiLabel;

    // ── Planet controls (Propulsion tab, batch 14) ──────────────────────────
    private HSlider _planetGravitySlider;
    private Label _planetRadiusLabel;
    private Label _planetAltitudeLabel;

    // ── FTL controls ──────────────────────────────────────────────────────────
    private CheckButton _ftlArmToggle;
    private HSlider _ftlProgressSlider;
    private OptionButton _ftlDestinationDropdown;
    private HSlider _ftlRangeSlider;
    private HSlider _ftlSignalLagSlider;
    private SpinBox _ftlPowerField;

    // ── Alerts ────────────────────────────────────────────────────────────────
    private CheckButton _alertOverheatToggle;
    private Label       _alertOverheatAckedLabel;
    private CheckButton _alertFtlAbortedToggle;
    private Label       _alertFtlAbortedAckedLabel;
    private CheckButton _alertAtmoDampenersToggle;
    private Label       _alertAtmoDampenersAckedLabel;
    private CheckButton _alertCollisionToggle;
    private Label       _alertCollisionAckedLabel;

    // ── Cameras ───────────────────────────────────────────────────────────────
    private OptionButton _cameraViewDropdown;

    // ── Ship / Global ─────────────────────────────────────────────────────────
    private CheckButton _loadoutUnlockedToggle;
    private SpinBox _missionTimeField;
    private LineEdit _callsignField;

    // ── Engineering — stub local state ────────────────────────────────────────
    private sealed class EngSysState
    {
        public string Id;
        public bool HasPower;
        public HSlider HealthSlider;
        public CheckButton DisabledCheck;
        public LineEdit EffectsEdit;
        public SpinBox PowerAllocBox;
        public SpinBox PowerMaxBox;
    }
    private readonly List<EngSysState> _engSystems = new();

    // ── Repair queue ──────────────────────────────────────────────────────────
    private OptionButton _repairSystemOpt;
    private ItemList _repairItemList;

    // ── Comms — stub local state ──────────────────────────────────────────────
    private sealed class CommsMsg
    {
        public string Id, Direction, Sender, Text;
        public int TimestampS;
    }
    private sealed class CommsContact
    {
        public string Id, Name, Alliance, VesselClass;
        public int RangeM;
    }
    private readonly List<CommsMsg> _commsMsgs = new();
    private readonly List<CommsContact> _commsContacts = new();
    private int _commsMsgNextId = 3;
    private int _commsContactNextId = 2;
    private VBoxContainer _commsMsgListBox;
    private VBoxContainer _commsContactListBox;

    // ── Turrets — stub local state ────────────────────────────────────────────
    private sealed class TurretState
    {
        public string Id;
        public CheckButton ArmedCheck;
        public OptionButton FireModeOption;
        public OptionButton LockStateOption;
        public HSlider BearingSlider;
        public CheckButton NoTargetCheck;
        public LineEdit TargetNameEdit, TargetClassEdit, TargetAllianceEdit;
        public SpinBox TargetRangeBox;
        public OptionButton AmmoLoadedOption;
        public SpinBox KineticCountBox, EmpCountBox;
        public HSlider HeatSlider;
    }
    private readonly List<TurretState> _turrets = new();

    // ── Missiles — stub local state ───────────────────────────────────────────
    private sealed class MissileState
    {
        public string Id;
        public CheckButton ArmedCheck;
        public OptionButton StatusOption, MissileTypeOption, LockStateOption;
        public LineEdit TargetNameEdit, TargetClassEdit, TargetAllianceEdit;
        public SpinBox TargetRangeBox;
    }
    private readonly List<MissileState> _missiles = new();

    // ── Hardpoints — real SimBus state (batch 12–13) ─────────────────────────
    private static readonly string[] HardpointCategories =
        { "empty", "utility_tool", "cargo_storage", "sensor_ew", "defense" };

    // Module names per category — must match the MQTT contract exactly.
    private static readonly (string category, string name)[] KnownModules =
    {
        ("utility_tool",  "Mining Laser"),
        ("utility_tool",  "Cutting/Welding Torch"),
        ("utility_tool",  "Grapple/Winch Rig"),
        ("cargo_storage", "Standard Pod"),
        ("cargo_storage", "Reefer Pod"),
        ("cargo_storage", "Ore Hopper"),
        ("sensor_ew",     "Long-range Scanner Array"),
        ("sensor_ew",     "Prospecting Suite"),
        ("sensor_ew",     "Stealth/ECM Package"),
        ("defense",       "Deflector Shield Generator"),
        ("defense",       "Point-Defense Turret Pod"),
        ("defense",       "Decoy/Flare Dispenser"),
    };

    private sealed class HardpointState
    {
        public int Slot;

        // Base controls (always visible)
        public OptionButton CategoryOption;
        public OptionButton ModuleOption;
        public CheckButton ArmedCheck;
        public CheckButton ActiveCheck;
        public HSlider IntensitySlider;

        // Utility group (visible when category == utility_tool)
        public Control UtilityGroup;
        public OptionButton ModeOption;
        public CheckButton AttachedCheck;

        // Cargo group (visible when category == cargo_storage)
        public Control CargoGroup;
        public HSlider FillPctSlider;
        public LineEdit ContentsEdit;
        public Control ReeferGroup;   // sub-group for Reefer Pod only
        public SpinBox TempCBox;
        public SpinBox TempMinBox;
        public SpinBox TempMaxBox;

        // Sensor group (visible when category == sensor_ew)
        public Control SensorGroup;
        public CheckButton ScannerModeActiveCheck;
        public CheckButton ScannerModeBeamCheck;
        public CheckButton StealthOnCheck;

        // Defense group (visible when category == defense)
        public Control DefenseGroup;
        public CheckButton ShieldOnCheck;
        public OptionButton ShieldFacingOption;
        public Dictionary<string, HSlider> ShieldStrengthSliders = new()
        {
            { "fore", null }, { "aft", null }, { "port", null }, { "starboard", null },
        };
        public CheckButton PdEngagedCheck;
        public CheckButton MissileLockWarningCheck;
        public SpinBox DecoyCountBox;
    }
    private readonly List<HardpointState> _hardpoints = new();

    // ═══════════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        Title = "Cold Orbit — Admin";
        var rect = WindowLayout.AdminPanelRect();
        Position = rect.Position;
        Size = rect.Size;

        var tabs = new TabContainer();
        tabs.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(tabs);

        AddTab(tabs, "Ship / Global",       BuildShipTab());
        AddTab(tabs, "Propulsion",          BuildPropulsionTab());
        AddTab(tabs, "FTL",                 BuildFtlTab());
        AddTab(tabs, "Alerts",              BuildAlertsTab());
        AddTab(tabs, "Engineering",         BuildEngineeringTab());
        AddTab(tabs, "Repair Queue",        BuildRepairQueueTab());
        AddTab(tabs, "Comms",               BuildCommsTab());
        AddTab(tabs, "Turrets",             BuildTurretsTab());
        AddTab(tabs, "Missiles",            BuildMissilesTab());
        AddTab(tabs, "Hardpoints",          BuildHardpointsTab());
        AddTab(tabs, "Cameras",             BuildCamerasTab());
        AddTab(tabs, "Touchscreen",         BuildStubTab());
        AddTab(tabs, "Generic Hardpoints",  BuildStubTab());

        SyncPropulsionFromBus();
        SyncFtlFromBus();
        SyncAlertsFromBus();
        SyncHardpointsFromBus();

        Show();
    }

    public override void _Process(double delta)
    {
        SyncPropulsionFromBus();
        SyncFtlFromBus();
        SyncAlertsFromBus();
        SyncHardpointsFromBus();
        SyncCamerasFromBus();
        SyncRepairQueueFromBus();
        SyncEngineeringFromBus();
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

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
        row.AddChild(new Label { Text = text, CustomMinimumSize = new Vector2(230, 0) });
        row.AddChild(control);
        return row;
    }

    private static HSlider MakeSlider(double min, double max, double value, double step = 0.01)
        => new HSlider
        {
            MinValue = min, MaxValue = max, Value = value, Step = step,
            CustomMinimumSize = new Vector2(180, 0),
        };

    private static SpinBox MakeSpinBox(double min, double max, double value, double step = 1)
        => new SpinBox
        {
            MinValue = min, MaxValue = max, Value = value, Step = step,
            CustomMinimumSize = new Vector2(120, 0),
        };

    private static OptionButton MakeOptions(string[] options, int selected = 0)
    {
        var opt = new OptionButton();
        foreach (var o in options) opt.AddItem(o);
        opt.Select(selected);
        return opt;
    }

    private static Control BuildStubTab()
    {
        var root = new VBoxContainer();
        root.AddChild(new Label { Text = "No sim state yet." });
        return root;
    }

    // ── Cameras tab ───────────────────────────────────────────────────────────

    private static readonly string[] CameraViewKeys =
        { "forward", "aft", "chase", "dorsal", "ventral", "docking", "damage" };

    private Control BuildCamerasTab()
    {
        var root = new VBoxContainer();

        _cameraViewDropdown = MakeOptions(new[] { "Forward", "Aft", "Chase", "Dorsal", "Ventral", "Docking", "Damage" });
        _cameraViewDropdown.ItemSelected += idx =>
        {
            if (_mirrorActive) return;
            if (SimBus.Instance != null)
                SimBus.Instance.Cameras.PendingView = CameraViewKeys[(int)idx];
        };
        root.AddChild(Labeled("Active View", _cameraViewDropdown));

        return root;
    }

    private void SyncCamerasFromBus()
    {
        if (_cameraViewDropdown == null) return;
        string view = SimBus.Instance?.Cameras?.ActiveView ?? "forward";
        int idx = System.Array.IndexOf(CameraViewKeys, view);
        if (idx < 0) idx = 0;
        if (_cameraViewDropdown.Selected == idx) return;
        _mirrorActive = true;
        _cameraViewDropdown.Select(idx);
        _mirrorActive = false;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ── Tab builders ──────────────────────────────────────────────────────────

    private Control BuildShipTab()
    {
        var root = new VBoxContainer();

        _loadoutUnlockedToggle = new CheckButton { Text = "Loadout Unlocked" };
        _loadoutUnlockedToggle.Toggled += pressed =>
            SimBus.Instance?.PublishAdminOverrideShipLoadout(pressed);
        root.AddChild(_loadoutUnlockedToggle);

        _missionTimeField = MakeSpinBox(0, 999999, 0);
        root.AddChild(Labeled("Mission Time (s)", _missionTimeField));

        _callsignField = new LineEdit { Text = "Cold Orbit", CustomMinimumSize = new Vector2(200, 0) };
        var callsignApply = new Button { Text = "Apply" };
        callsignApply.Pressed += () =>
            SimBus.Instance?.PublishAdminOverrideShipCallsign(_callsignField.Text);
        root.AddChild(Labeled("Callsign", Row(_callsignField, callsignApply)));

        root.AddChild(new HSeparator());

        var resetBtn = new Button { Text = "Reset to spawn" };
        resetBtn.Pressed += () => SimBus.Instance?.AdminResetToSpawn();
        root.AddChild(resetBtn);

        return root;
    }

    private Control BuildPropulsionTab()
    {
        var root = new VBoxContainer();

        _propArmToggle = new CheckButton { Text = "Armed (display-only — no sim backing yet)" };
        root.AddChild(_propArmToggle);

        _propThrottleSlider = MakeSlider(0, 1, 0);
        root.AddChild(Labeled("Throttle (display-only)", _propThrottleSlider));

        _propMixSlider = MakeSlider(0, 1, 0);
        _propMixSlider.ValueChanged += v =>
            SimBus.Instance.Propulsion.MixTarget = (float)v;
        root.AddChild(Labeled("Mix (Economy ↔ Power)", _propMixSlider));

        _propRcsToggle = new CheckButton { Text = "RCS" };
        _propRcsToggle.Toggled += pressed =>
            SimBus.Instance.Propulsion.RcsEnabled = pressed;
        root.AddChild(_propRcsToggle);

        _propDampenerToggle = new CheckButton { Text = "Dampeners" };
        _propDampenerToggle.Toggled += pressed =>
            SimBus.Instance.Propulsion.DampenersEnabled = pressed;
        root.AddChild(_propDampenerToggle);

        _propDampenerModeLabel = new Label { Text = "off" };
        root.AddChild(Labeled("Dampener mode (display-only)", _propDampenerModeLabel));

        _propReverseToggle = new CheckButton { Text = "Reverse (display-only)" };
        root.AddChild(_propReverseToggle);

        _propEngineTempSlider = MakeSlider(0, 1000, 0, 1);
        _propEngineTempSlider.ValueChanged += v =>
            SimBus.Instance?.PublishAdminOverridePropulsionTemp((float)v, SimBus.Instance.Propulsion.SoiBody);
        root.AddChild(Labeled("Engine Temp °C (override; ≤1 tick)", _propEngineTempSlider));

        _propThrustMultSlider = MakeSlider(1, 50, 1, 1);
        _propThrustMultSlider.ValueChanged += v =>
            SimBus.Instance.Propulsion.AdminThrustMultiplier = (float)v;
        root.AddChild(Labeled("Thrust multiplier ×1–50 (testing)", _propThrustMultSlider));

        _propOvertempBypassToggle = new CheckButton { Text = "Bypass overtemp cutoff" };
        _propOvertempBypassToggle.Toggled += pressed =>
            SimBus.Instance.Propulsion.AdminOvertempBypass = pressed;
        root.AddChild(_propOvertempBypassToggle);

        _propVelocityLabel = new Label { Text = "0.00 m/s" };
        root.AddChild(Labeled("Velocity (display-only)", _propVelocityLabel));

        _propAltitudeLabel = new Label { Text = "0 m" };
        root.AddChild(Labeled("Altitude (display-only)", _propAltitudeLabel));

        _propCollisionForceLabel = new Label { Text = "0.0 N" };
        root.AddChild(Labeled("Hull impact force (display-only)", _propCollisionForceLabel));

        var simCollisionBtn = new Button { Text = "Simulate collision" };
        simCollisionBtn.Pressed += () => SimBus.Instance?.AdminTriggerCollisionAlert();
        root.AddChild(simCollisionBtn);

        var simAtmoBtn = new CheckButton { Text = "Simulate atmosphere entry" };
        simAtmoBtn.Toggled += pressed => SimBus.Instance?.AdminSimulateAtmosphere(pressed);
        root.AddChild(simAtmoBtn);

        _propSoiLabel = new Label { Text = "Deep Space" };
        root.AddChild(Labeled("SOI Body (display-only)", _propSoiLabel));

        root.AddChild(new HSeparator());
        root.AddChild(new Label { Text = "── Planet ──" });

        _planetGravitySlider = MakeSlider(0, 20, 9.8, 0.1);
        _planetGravitySlider.ValueChanged += v =>
            SimBus.Instance?.AdminSetPlanetGravity((float)v);
        root.AddChild(Labeled("Surface Gravity m/s² (0–20)", _planetGravitySlider));

        _planetRadiusLabel = new Label { Text = "0 m" };
        root.AddChild(Labeled("Planet Radius (display-only)", _planetRadiusLabel));

        _planetAltitudeLabel = new Label { Text = "0 m" };
        root.AddChild(Labeled("Distance to surface (display-only)", _planetAltitudeLabel));

        return root;
    }

    private Control BuildFtlTab()
    {
        var root = new VBoxContainer();

        _ftlArmToggle = new CheckButton { Text = "Armed" };
        _ftlArmToggle.Toggled += pressed =>
            SimBus.Instance.Ftl.Armed = pressed;
        root.AddChild(_ftlArmToggle);

        _ftlProgressSlider = MakeSlider(0, 1, 0);
        root.AddChild(Labeled("Progress (display-only)", _ftlProgressSlider));

        // Flat destination picker over the whole Drift star map: all 26 stars
        // in A–Z order, then every planet grouped by system. The physical panel
        // only has prev/next, so this dropdown is the admin shortcut for
        // jumping straight to any entry.
        _ftlDestinationDropdown = MakeOptions(
            DriftData.Destinations
                .Select(d => d.IsStar ? d.Name : $"    {d.Name} ({DriftData.GetSystem(d.SystemId).StarName})")
                .ToArray());
        _ftlDestinationDropdown.ItemSelected += idx =>
        {
            if (_mirrorActive) return;
            SimBus.Instance?.Ftl.SelectTo(DriftData.Destinations[(int)idx]);
            SimBus.Instance?.PublishFtlSystem();
            SimBus.Instance?.PublishFtlNavTarget();
        };
        root.AddChild(Labeled("Destination", _ftlDestinationDropdown));

        var instantJumpBtn = new Button { Text = "Instant Jump (skip FTL)" };
        instantJumpBtn.Pressed += () =>
        {
            int idx = _ftlDestinationDropdown.Selected;
            if (idx < 0 || idx >= DriftData.Destinations.Length) return;
            var dest = DriftData.Destinations[idx];
            SimBus.Instance.Ftl.PendingAdminReset = true;
            Callable.From(() => SceneManager.Instance.LoadSoI(dest, Vector3.Zero)).CallDeferred();
        };
        root.AddChild(instantJumpBtn);

        _ftlRangeSlider = MakeSlider(0, 20, 0, 0.01);
        root.AddChild(Labeled("Range AU (display-only)", _ftlRangeSlider));

        _ftlSignalLagSlider = MakeSlider(0, 8, 0, 0.01);
        root.AddChild(Labeled("Signal Lag s (display-only)", _ftlSignalLagSlider));

        _ftlPowerField = MakeSpinBox(0, 10000, 0);
        root.AddChild(Labeled("Power kW (display-only)", _ftlPowerField));

        return root;
    }

    private Control BuildAlertsTab()
    {
        var root = new VBoxContainer();

        _alertOverheatToggle = new CheckButton { Text = "ENGINE OVERHEAT" };
        _alertOverheatToggle.Toggled += pressed =>
            ToggleAlert("alert_engines_overheat", "warning", "engines", "ENGINE OVERHEAT", pressed);
        _alertOverheatAckedLabel = new Label { Text = "" };
        var ackOverheatBtn = new Button { Text = "Acknowledge" };
        ackOverheatBtn.Pressed += () => AcknowledgeAlert("alert_engines_overheat");
        root.AddChild(Row(_alertOverheatToggle, _alertOverheatAckedLabel, ackOverheatBtn));

        _alertFtlAbortedToggle = new CheckButton { Text = "FTL CHARGE ABORTED" };
        _alertFtlAbortedToggle.Toggled += pressed =>
            ToggleAlert("alert_ftl_aborted", "caution", "ftl", "FTL CHARGE ABORTED", pressed);
        _alertFtlAbortedAckedLabel = new Label { Text = "" };
        var ackFtlBtn = new Button { Text = "Acknowledge" };
        ackFtlBtn.Pressed += () => AcknowledgeAlert("alert_ftl_aborted");
        root.AddChild(Row(_alertFtlAbortedToggle, _alertFtlAbortedAckedLabel, ackFtlBtn));

        _alertCollisionToggle = new CheckButton { Text = "HULL IMPACT" };
        _alertCollisionToggle.Toggled += pressed =>
            ToggleAlert("alert_collision", "caution", "hull", "HULL IMPACT", pressed);
        _alertCollisionAckedLabel = new Label { Text = "" };
        var ackCollisionBtn = new Button { Text = "Acknowledge" };
        ackCollisionBtn.Pressed += () => AcknowledgeAlert("alert_collision");
        root.AddChild(Row(_alertCollisionToggle, _alertCollisionAckedLabel, ackCollisionBtn));

        _alertAtmoDampenersToggle = new CheckButton { Text = "PROXIMITY ALERT" };
        _alertAtmoDampenersToggle.Toggled += pressed =>
            ToggleAlert("alert_atmo_dampeners_inop", "caution", "propulsion", "PROXIMITY ALERT", pressed);
        _alertAtmoDampenersAckedLabel = new Label { Text = "" };
        var ackAtmoBtn = new Button { Text = "Acknowledge" };
        ackAtmoBtn.Pressed += () => AcknowledgeAlert("alert_atmo_dampeners_inop");
        root.AddChild(Row(_alertAtmoDampenersToggle, _alertAtmoDampenersAckedLabel, ackAtmoBtn));

        root.AddChild(new HSeparator());
        root.AddChild(new Label { Text = "─ new alerts appear here as systems get wired ─" });

        return root;
    }

    private void AcknowledgeAlert(string id)
    {
        var alerts = SimBus.Instance?.Alerts;
        if (alerts == null) return;
        for (int i = 0; i < alerts.Active.Count; i++)
        {
            if (alerts.Active[i].Id == id && !alerts.Active[i].Acknowledged)
                alerts.Active[i] = alerts.Active[i] with { Acknowledged = true };
        }
        SimBus.Instance?.PublishCurrentAlerts();
    }

    private void ToggleAlert(string id, string severity, string system, string message, bool active)
    {
        var alerts = SimBus.Instance?.Alerts;
        if (alerts == null) return;
        if (active && !alerts.Active.Any(a => a.Id == id))
            alerts.Active.Add(new SimBus.AlertEntry(id, severity, system, message, 0));
        else if (!active)
            alerts.Active.RemoveAll(a => a.Id == id);
        SimBus.Instance?.PublishCurrentAlerts();
    }

    private Control BuildEngineeringTab()
    {
        var root = new VBoxContainer();

        var systemDefs = new (string id, bool hasPower)[]
        {
            ("weapons",   true),  ("engines",   true),  ("ftl",       true),
            ("reactor",   false), ("utility_1", true),  ("utility_2", true),
            ("utility_3", true),  ("utility_4", true),  ("hull",      false),
        };

        foreach (var (id, hasPower) in systemDefs)
        {
            var sys = new EngSysState { Id = id, HasPower = hasPower };
            root.AddChild(new Label { Text = $"── {id} ──" });

            sys.HealthSlider = MakeSlider(0, 100, 100, 1);
            sys.HealthSlider.ValueChanged += _ => PublishEngSys(sys);
            root.AddChild(Labeled("Health", sys.HealthSlider));

            // Read-only indicator: shows Disabled derived from health == 0.
            // Not wired to PublishEngSys — drag the slider to 0 to disable.
            sys.DisabledCheck = new CheckButton { Text = "Disabled", Disabled = true };
            root.AddChild(sys.DisabledCheck);

            sys.EffectsEdit = new LineEdit
            {
                PlaceholderText = "comma-separated",
                CustomMinimumSize = new Vector2(220, 0),
            };
            sys.EffectsEdit.TextChanged += _ => PublishEngSys(sys);
            root.AddChild(Labeled("Effects", sys.EffectsEdit));

            if (hasPower)
            {
                sys.PowerAllocBox = MakeSpinBox(0, 9999, 200);
                sys.PowerAllocBox.ValueChanged += _ => PublishEngSys(sys);
                root.AddChild(Labeled("Power Allocated kW", sys.PowerAllocBox));

                sys.PowerMaxBox = MakeSpinBox(0, 9999, 500);
                sys.PowerMaxBox.ValueChanged += _ => PublishEngSys(sys);
                root.AddChild(Labeled("Power Max kW", sys.PowerMaxBox));
            }
            else
            {
                root.AddChild(Labeled("Power", new Label { Text = "(none — hull/reactor)" }));
            }

            root.AddChild(new HSeparator());
            _engSystems.Add(sys);
        }

        return root;
    }

    private void PublishEngSys(EngSysState sys)
    {
        if (_mirrorActive) return;
        int health = (int)sys.HealthSlider.Value;
        int? powerAlloc = sys.HasPower ? (int?)((int)sys.PowerAllocBox.Value) : null;
        int? powerMax   = sys.HasPower ? (int?)((int)sys.PowerMaxBox.Value)   : null;
        var effects = sys.EffectsEdit.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // disabled is derived from health — the checkbox is a read-only indicator.
        SimBus.Instance?.PublishAdminOverrideEngineering(
            sys.Id, health, disabled: health == 0, effects, powerAlloc, powerMax);
    }

    // Mirrors live EngineeringState health/disabled into the admin sliders so
    // repair progress is reflected without the operator touching the controls.
    private void SyncEngineeringFromBus()
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;
        _mirrorActive = true;
        foreach (var sys in _engSystems)
        {
            var record = eng.GetById(sys.Id);
            sys.HealthSlider.Value = record.Health;
            sys.DisabledCheck.SetPressedNoSignal(record.Disabled);
        }
        _mirrorActive = false;
    }

    private Control BuildRepairQueueTab()
    {
        var root = new VBoxContainer();

        root.AddChild(new Label { Text = "── Repair Queue ──" });
        root.AddChild(new Label { Text = "Queue is populated automatically when subsystems take damage.\nUse the buttons below to reorder or remove entries." });

        _repairSystemOpt = MakeOptions(new[]
        {
            "weapons", "engines", "ftl", "reactor",
            "utility_1", "utility_2", "utility_3", "utility_4", "hull",
        }, 1);
        var enqueueBtn = new Button { Text = "Add to Queue" };
        enqueueBtn.Pressed += OnRepairEnqueue;
        root.AddChild(Row(_repairSystemOpt, enqueueBtn));

        root.AddChild(new HSeparator());

        _repairItemList = new ItemList { CustomMinimumSize = new Vector2(420, 200) };
        root.AddChild(_repairItemList);

        var removeBtn = new Button { Text = "Remove Selected" };
        removeBtn.Pressed += OnRepairRemoveSelected;
        var upBtn = new Button { Text = "Move Up" };
        upBtn.Pressed += () => OnRepairMove(-1);
        var downBtn = new Button { Text = "Move Down" };
        downBtn.Pressed += () => OnRepairMove(1);
        root.AddChild(Row(removeBtn, upBtn, downBtn));

        SyncRepairQueueFromBus();
        return root;
    }

    // Add a system to the real repair queue (no-op if already queued).
    private void OnRepairEnqueue()
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;
        string sysId = _repairSystemOpt.GetItemText(_repairSystemOpt.Selected);
        if (!eng.RepairQueue.Contains(sysId))
        {
            eng.RepairQueue.Add(sysId);
            SimBus.Instance.PublishRepairQueue();
        }
        SyncRepairQueueFromBus();
    }

    private void OnRepairRemoveSelected()
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;
        int[] sel = _repairItemList.GetSelectedItems();
        if (sel.Length == 0) return;
        eng.RepairQueue.RemoveAt(sel[0]);
        SimBus.Instance.PublishRepairQueue();
        SyncRepairQueueFromBus();
    }

    private void OnRepairMove(int dir)
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;
        int[] sel = _repairItemList.GetSelectedItems();
        if (sel.Length == 0) return;
        int i = sel[0];
        int j = i + dir;
        if (j < 0 || j >= eng.RepairQueue.Count) return;
        (eng.RepairQueue[i], eng.RepairQueue[j]) = (eng.RepairQueue[j], eng.RepairQueue[i]);
        SimBus.Instance.PublishRepairQueue();
        SyncRepairQueueFromBus();
        _repairItemList.Select(j);
    }

    // Refreshes the repair item list from live EngineeringState. Called from
    // _Process every frame so health and ETA stay current as repair ticks.
    private void SyncRepairQueueFromBus()
    {
        var eng = SimBus.Instance?.Engineering;
        if (eng == null) return;
        int prevSel = _repairItemList.GetSelectedItems() is { Length: > 0 } s ? s[0] : -1;
        _repairItemList.Clear();
        for (int i = 0; i < eng.RepairQueue.Count; i++)
        {
            var sys = eng.GetById(eng.RepairQueue[i]);
            string status = i == 0 ? "in_progress" : "queued";
            string eta = sys.RepairEtaSeconds.HasValue ? $"{(int)sys.RepairEtaSeconds.Value}s" : "—";
            _repairItemList.AddItem($"{sys.Id}  [{status}]  HP {sys.Health}  ETA {eta}");
        }
        if (prevSel >= 0 && prevSel < eng.RepairQueue.Count)
            _repairItemList.Select(prevSel);
    }

    private Control BuildCommsTab()
    {
        var root = new VBoxContainer();

        root.AddChild(new Label { Text = "── Message Log ──" });
        _commsMsgListBox = new VBoxContainer();
        root.AddChild(_commsMsgListBox);

        _commsMsgs.Add(new CommsMsg
        {
            Id = "msg_001", Direction = "incoming", Sender = "Harlan Voss",
            Text = "Cold Orbit, this is Voss. You in position?", TimestampS = 3600,
        });
        _commsMsgs.Add(new CommsMsg
        {
            Id = "msg_002", Direction = "outgoing", Sender = "player",
            Text = "Affirmative. Holding at waypoint delta.", TimestampS = 3618,
        });
        RefreshCommsMsgList();

        root.AddChild(new Label { Text = "Add Message:" });
        var dirOpt   = MakeOptions(new[] { "incoming", "outgoing" });
        var sender   = new LineEdit { PlaceholderText = "Sender",       CustomMinimumSize = new Vector2(200, 0) };
        var msgText  = new LineEdit { PlaceholderText = "Message text", CustomMinimumSize = new Vector2(300, 0) };
        root.AddChild(Labeled("Direction", dirOpt));
        root.AddChild(Labeled("Sender",    sender));
        root.AddChild(Labeled("Text",      msgText));
        var addMsgBtn = new Button { Text = "Add" };
        addMsgBtn.Pressed += () =>
        {
            _commsMsgs.Add(new CommsMsg
            {
                Id         = $"msg_{_commsMsgNextId++:000}",
                Direction  = dirOpt.GetItemText(dirOpt.Selected),
                Sender     = sender.Text,
                Text       = msgText.Text,
                TimestampS = (int)(_missionTimeField?.Value ?? 0),
            });
            RefreshCommsMsgList();
            SimBus.Instance?.PublishAdminOverrideCommsLog(BuildCommsLogPayload());
        };
        root.AddChild(addMsgBtn);

        var clearBtn = new Button { Text = "Clear Log" };
        clearBtn.Pressed += () =>
        {
            _commsMsgs.Clear();
            RefreshCommsMsgList();
            SimBus.Instance?.PublishAdminOverrideCommsLog(BuildCommsLogPayload());
        };
        root.AddChild(clearBtn);

        root.AddChild(new HSeparator());
        root.AddChild(new Label { Text = "── Contacts ──" });
        _commsContactListBox = new VBoxContainer();
        root.AddChild(_commsContactListBox);

        _commsContacts.Add(new CommsContact
        {
            Id = "contact_001", Name = "Harlan Voss",
            Alliance = "Independent", VesselClass = "Light Freighter", RangeM = 1240,
        });
        RefreshCommsContactList();

        root.AddChild(new Label { Text = "Add Contact:" });
        var cName     = new LineEdit { PlaceholderText = "Name",         CustomMinimumSize = new Vector2(200, 0) };
        var cAlliance = new LineEdit { PlaceholderText = "Alliance",     CustomMinimumSize = new Vector2(200, 0) };
        var cClass    = new LineEdit { PlaceholderText = "Vessel class", CustomMinimumSize = new Vector2(200, 0) };
        var cRange    = MakeSpinBox(0, 999999, 1000);
        root.AddChild(Labeled("Name",         cName));
        root.AddChild(Labeled("Alliance",     cAlliance));
        root.AddChild(Labeled("Vessel Class", cClass));
        root.AddChild(Labeled("Range m",      cRange));
        var addContactBtn = new Button { Text = "Add Contact" };
        addContactBtn.Pressed += () =>
        {
            _commsContacts.Add(new CommsContact
            {
                Id          = $"contact_{_commsContactNextId++:000}",
                Name        = cName.Text,
                Alliance    = cAlliance.Text,
                VesselClass = cClass.Text,
                RangeM      = (int)cRange.Value,
            });
            RefreshCommsContactList();
            SimBus.Instance?.PublishAdminOverrideCommsTargets(BuildCommsTargetsPayload());
        };
        root.AddChild(addContactBtn);

        return root;
    }

    private void RefreshCommsMsgList()
    {
        foreach (Node c in _commsMsgListBox.GetChildren()) c.QueueFree();
        foreach (var msg in _commsMsgs.ToList())
        {
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"[{msg.Direction}] {msg.Sender}: {msg.Text} (t={msg.TimestampS}s)",
                CustomMinimumSize = new Vector2(560, 0),
            });
            var del = new Button { Text = "×" };
            var m = msg;
            del.Pressed += () =>
            {
                _commsMsgs.Remove(m);
                RefreshCommsMsgList();
                SimBus.Instance?.PublishAdminOverrideCommsLog(BuildCommsLogPayload());
            };
            row.AddChild(del);
            _commsMsgListBox.AddChild(row);
        }
    }

    private void RefreshCommsContactList()
    {
        foreach (Node c in _commsContactListBox.GetChildren()) c.QueueFree();
        foreach (var contact in _commsContacts.ToList())
        {
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{contact.Name} [{contact.Alliance}] {contact.VesselClass} @ {contact.RangeM}m",
                CustomMinimumSize = new Vector2(460, 0),
            });
            var del = new Button { Text = "×" };
            var c = contact;
            del.Pressed += () =>
            {
                _commsContacts.Remove(c);
                RefreshCommsContactList();
                SimBus.Instance?.PublishAdminOverrideCommsTargets(BuildCommsTargetsPayload());
            };
            row.AddChild(del);
            _commsContactListBox.AddChild(row);
        }
    }

    private object[] BuildCommsLogPayload()
        => _commsMsgs.Select(m => (object)new
        {
            id = m.Id, direction = m.Direction, sender = m.Sender,
            text = m.Text, timestamp_s = m.TimestampS,
        }).ToArray();

    private object[] BuildCommsTargetsPayload()
        => _commsContacts.Select(c => (object)new
        {
            id = c.Id, name = c.Name, alliance = c.Alliance,
            vessel_class = c.VesselClass, range_m = c.RangeM,
        }).ToArray();

    private Control BuildTurretsTab()
    {
        var root = new VBoxContainer();

        foreach (var turretId in new[] { "dorsal", "ventral" })
        {
            var t = new TurretState { Id = turretId };
            root.AddChild(new Label { Text = $"── {turretId} ──" });

            t.ArmedCheck = new CheckButton { Text = "Armed" };
            t.ArmedCheck.Toggled += _ => PublishTurret(t);
            root.AddChild(t.ArmedCheck);

            t.FireModeOption = MakeOptions(new[] { "lethal", "non_lethal" });
            t.FireModeOption.ItemSelected += _ => { if (!_mirrorActive) PublishTurret(t); };
            root.AddChild(Labeled("Fire Mode", t.FireModeOption));

            t.LockStateOption = MakeOptions(new[] { "none", "acquiring", "locked" });
            t.LockStateOption.ItemSelected += _ => { if (!_mirrorActive) PublishTurret(t); };
            root.AddChild(Labeled("Lock State", t.LockStateOption));

            t.BearingSlider = MakeSlider(0, 360, 0, 1);
            t.BearingSlider.ValueChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Bearing °", t.BearingSlider));

            t.NoTargetCheck = new CheckButton { Text = "No Target (bearing_deg = null)" };
            t.NoTargetCheck.ButtonPressed = true;
            t.NoTargetCheck.Toggled += _ => PublishTurret(t);
            root.AddChild(t.NoTargetCheck);

            t.TargetNameEdit = new LineEdit { CustomMinimumSize = new Vector2(200, 0) };
            t.TargetNameEdit.TextChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Target Name", t.TargetNameEdit));

            t.TargetClassEdit = new LineEdit { CustomMinimumSize = new Vector2(200, 0) };
            t.TargetClassEdit.TextChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Target Class", t.TargetClassEdit));

            t.TargetAllianceEdit = new LineEdit { CustomMinimumSize = new Vector2(200, 0) };
            t.TargetAllianceEdit.TextChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Target Alliance", t.TargetAllianceEdit));

            t.TargetRangeBox = MakeSpinBox(0, 999999, 0);
            t.TargetRangeBox.ValueChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Target Range m (0 = null)", t.TargetRangeBox));

            t.AmmoLoadedOption = MakeOptions(new[] { "Kinetic Slug", "EMP Round", "Incendiary", "Tracer", "Flechette" });
            t.AmmoLoadedOption.ItemSelected += _ => { if (!_mirrorActive) PublishTurret(t); };
            root.AddChild(Labeled("Ammo Loaded", t.AmmoLoadedOption));

            t.KineticCountBox = MakeSpinBox(0, 9999, 142);
            t.KineticCountBox.ValueChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Kinetic Slug Count", t.KineticCountBox));

            t.EmpCountBox = MakeSpinBox(0, 9999, 28);
            t.EmpCountBox.ValueChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("EMP Round Count", t.EmpCountBox));

            t.HeatSlider = MakeSlider(0, 1, 0);
            t.HeatSlider.ValueChanged += _ => PublishTurret(t);
            root.AddChild(Labeled("Heat", t.HeatSlider));

            root.AddChild(new HSeparator());
            _turrets.Add(t);
        }

        return root;
    }

    private void PublishTurret(TurretState t)
    {
        float? bearing    = t.NoTargetCheck.ButtonPressed ? (float?)null : (float)t.BearingSlider.Value;
        string? tgtName   = NullIfEmpty(t.TargetNameEdit.Text);
        string? tgtClass  = NullIfEmpty(t.TargetClassEdit.Text);
        string? tgtAllied = NullIfEmpty(t.TargetAllianceEdit.Text);
        int?    tgtRange  = (int)t.TargetRangeBox.Value > 0 ? (int)t.TargetRangeBox.Value : (int?)null;
        SimBus.Instance?.PublishAdminOverrideTurret(
            t.Id,
            t.ArmedCheck.ButtonPressed,
            t.FireModeOption.GetItemText(t.FireModeOption.Selected),
            t.LockStateOption.GetItemText(t.LockStateOption.Selected),
            bearing, tgtName, tgtClass, tgtAllied, tgtRange,
            t.AmmoLoadedOption.GetItemText(t.AmmoLoadedOption.Selected),
            (int)t.KineticCountBox.Value,
            (int)t.EmpCountBox.Value,
            (float)t.HeatSlider.Value);
    }

    private Control BuildMissilesTab()
    {
        var root = new VBoxContainer();

        foreach (var tubeId in new[] { "fore_port", "fore_starboard", "aft_port", "aft_starboard" })
        {
            var m = new MissileState { Id = tubeId };
            root.AddChild(new Label { Text = $"── {tubeId} ──" });

            m.ArmedCheck = new CheckButton { Text = "Armed" };
            m.ArmedCheck.Toggled += _ => PublishMissile(m);
            root.AddChild(m.ArmedCheck);

            m.StatusOption = MakeOptions(new[] { "loaded", "empty", "reloading" });
            m.StatusOption.ItemSelected += _ => { if (!_mirrorActive) PublishMissile(m); };
            root.AddChild(Labeled("Status", m.StatusOption));

            m.MissileTypeOption = MakeOptions(
                new[] { "Dumbfire", "Seeking", "EMP Burst", "Fragmentation", "Armour Piercing" }, 1);
            m.MissileTypeOption.ItemSelected += _ => { if (!_mirrorActive) PublishMissile(m); };
            root.AddChild(Labeled("Missile Type", m.MissileTypeOption));

            m.LockStateOption = MakeOptions(new[] { "none", "acquiring", "locked" });
            m.LockStateOption.ItemSelected += _ => { if (!_mirrorActive) PublishMissile(m); };
            root.AddChild(Labeled("Lock State", m.LockStateOption));

            m.TargetNameEdit = new LineEdit { CustomMinimumSize = new Vector2(200, 0) };
            m.TargetNameEdit.TextChanged += _ => PublishMissile(m);
            root.AddChild(Labeled("Target Name", m.TargetNameEdit));

            m.TargetClassEdit = new LineEdit { CustomMinimumSize = new Vector2(200, 0) };
            m.TargetClassEdit.TextChanged += _ => PublishMissile(m);
            root.AddChild(Labeled("Target Class", m.TargetClassEdit));

            m.TargetAllianceEdit = new LineEdit { CustomMinimumSize = new Vector2(200, 0) };
            m.TargetAllianceEdit.TextChanged += _ => PublishMissile(m);
            root.AddChild(Labeled("Target Alliance", m.TargetAllianceEdit));

            m.TargetRangeBox = MakeSpinBox(0, 999999, 0);
            m.TargetRangeBox.ValueChanged += _ => PublishMissile(m);
            root.AddChild(Labeled("Target Range m (0 = null)", m.TargetRangeBox));

            root.AddChild(new HSeparator());
            _missiles.Add(m);
        }

        return root;
    }

    private void PublishMissile(MissileState m)
    {
        string status = m.StatusOption.GetItemText(m.StatusOption.Selected);
        string? missileType = status == "empty"
            ? null
            : m.MissileTypeOption.GetItemText(m.MissileTypeOption.Selected);
        string? tgtName   = NullIfEmpty(m.TargetNameEdit.Text);
        string? tgtClass  = NullIfEmpty(m.TargetClassEdit.Text);
        string? tgtAllied = NullIfEmpty(m.TargetAllianceEdit.Text);
        int?    tgtRange  = (int)m.TargetRangeBox.Value > 0 ? (int)m.TargetRangeBox.Value : (int?)null;
        SimBus.Instance?.PublishAdminOverrideMissile(
            m.Id, m.ArmedCheck.ButtonPressed, status, missileType,
            m.LockStateOption.GetItemText(m.LockStateOption.Selected),
            tgtName, tgtClass, tgtAllied, tgtRange);
    }

    private Control BuildHardpointsTab()
    {
        var root = new VBoxContainer();

        for (int slot = 1; slot <= 4; slot++)
        {
            var h = new HardpointState { Slot = slot };
            root.AddChild(new Label { Text = $"── Slot {slot} ──" });

            // ── Base controls (always visible) ─────────────────────────────
            h.CategoryOption = MakeOptions(HardpointCategories);
            h.CategoryOption.ItemSelected += _ =>
            {
                if (_mirrorActive) return;
                RefreshModuleDropdown(h);
                UpdateHardpointControlVisibility(h);
                WriteHardpointToSimBus(h);
            };
            root.AddChild(Labeled("Category", h.CategoryOption));

            h.ModuleOption = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            h.ModuleOption.ItemSelected += _ =>
            {
                if (_mirrorActive) return;
                UpdateHardpointControlVisibility(h);
                WriteHardpointToSimBus(h);
            };
            root.AddChild(Labeled("Module", h.ModuleOption));

            h.ArmedCheck = new CheckButton { Text = "Armed" };
            h.ArmedCheck.Toggled += _ => WriteHardpointToSimBus(h);
            root.AddChild(h.ArmedCheck);

            h.ActiveCheck = new CheckButton { Text = "Active (firing/running)" };
            h.ActiveCheck.Toggled += _ => WriteHardpointToSimBus(h);
            root.AddChild(h.ActiveCheck);

            h.IntensitySlider = MakeSlider(0, 1, 0);
            h.IntensitySlider.ValueChanged += _ => WriteHardpointToSimBus(h);
            root.AddChild(Labeled("Intensity / Index (0–1)", h.IntensitySlider));

            // ── Utility group ──────────────────────────────────────────────
            var utilGroup = new VBoxContainer();
            h.UtilityGroup = utilGroup;

            h.ModeOption = MakeOptions(new[] { "—", "weld", "cut" });
            h.ModeOption.ItemSelected += _ => { if (!_mirrorActive) WriteHardpointToSimBus(h); };
            utilGroup.AddChild(Labeled("Mode (torch only)", h.ModeOption));

            h.AttachedCheck = new CheckButton { Text = "Attached (grapple only)" };
            h.AttachedCheck.Toggled += _ => WriteHardpointToSimBus(h);
            utilGroup.AddChild(h.AttachedCheck);

            root.AddChild(utilGroup);

            // ── Cargo group ────────────────────────────────────────────────
            var cargoGroup = new VBoxContainer();
            h.CargoGroup = cargoGroup;

            h.FillPctSlider = MakeSlider(0, 100, 0, 1);
            h.FillPctSlider.ValueChanged += _ => WriteHardpointToSimBus(h);
            cargoGroup.AddChild(Labeled("Fill %", h.FillPctSlider));

            h.ContentsEdit = new LineEdit
            {
                PlaceholderText = "cargo contents",
                CustomMinimumSize = new Vector2(220, 0),
            };
            h.ContentsEdit.TextChanged += _ => { if (!_mirrorActive) WriteHardpointToSimBus(h); };
            cargoGroup.AddChild(Labeled("Contents", h.ContentsEdit));

            var reeferGroup = new VBoxContainer();
            h.ReeferGroup = reeferGroup;

            h.TempCBox = MakeSpinBox(-50, 50, 4, 0.5);
            h.TempCBox.ValueChanged += _ => WriteHardpointToSimBus(h);
            reeferGroup.AddChild(Labeled("Temp °C", h.TempCBox));

            h.TempMinBox = MakeSpinBox(-50, 50, 2, 0.5);
            h.TempMinBox.ValueChanged += _ => WriteHardpointToSimBus(h);
            reeferGroup.AddChild(Labeled("Temp Min °C", h.TempMinBox));

            h.TempMaxBox = MakeSpinBox(-50, 50, 8, 0.5);
            h.TempMaxBox.ValueChanged += _ => WriteHardpointToSimBus(h);
            reeferGroup.AddChild(Labeled("Temp Max °C", h.TempMaxBox));

            cargoGroup.AddChild(reeferGroup);
            root.AddChild(cargoGroup);

            // ── Sensor group ───────────────────────────────────────────────
            var sensorGroup = new VBoxContainer();
            h.SensorGroup = sensorGroup;

            h.ScannerModeActiveCheck = new CheckButton { Text = "Scanner Active (vs Passive)" };
            h.ScannerModeActiveCheck.Toggled += _ => WriteHardpointToSimBus(h);
            sensorGroup.AddChild(h.ScannerModeActiveCheck);

            h.ScannerModeBeamCheck = new CheckButton { Text = "Scanner Beam (vs Pulse)" };
            h.ScannerModeBeamCheck.Toggled += _ => WriteHardpointToSimBus(h);
            sensorGroup.AddChild(h.ScannerModeBeamCheck);

            h.StealthOnCheck = new CheckButton { Text = "Stealth/ECM On" };
            h.StealthOnCheck.Toggled += _ => WriteHardpointToSimBus(h);
            sensorGroup.AddChild(h.StealthOnCheck);

            root.AddChild(sensorGroup);

            // ── Defense group ──────────────────────────────────────────────
            var defenseGroup = new VBoxContainer();
            h.DefenseGroup = defenseGroup;

            h.ShieldOnCheck = new CheckButton { Text = "Shield On" };
            h.ShieldOnCheck.Toggled += _ => WriteHardpointToSimBus(h);
            defenseGroup.AddChild(h.ShieldOnCheck);

            h.ShieldFacingOption = MakeOptions(new[] { "fore", "aft", "port", "starboard" });
            h.ShieldFacingOption.ItemSelected += _ =>
            {
                if (!_mirrorActive) WriteHardpointToSimBus(h);
            };
            defenseGroup.AddChild(Labeled("Shield Facing", h.ShieldFacingOption));

            foreach (var facing in new[] { "fore", "aft", "port", "starboard" })
            {
                var slider = MakeSlider(0, 1, 0.5);
                var f = facing;
                slider.ValueChanged += _ => WriteHardpointToSimBus(h);
                h.ShieldStrengthSliders[f] = slider;
                defenseGroup.AddChild(Labeled($"Shield {facing} str", slider));
            }

            h.PdEngagedCheck = new CheckButton { Text = "PD Engaged" };
            h.PdEngagedCheck.Toggled += _ => WriteHardpointToSimBus(h);
            defenseGroup.AddChild(h.PdEngagedCheck);

            h.MissileLockWarningCheck = new CheckButton { Text = "Missile Lock Warning (test-only)" };
            h.MissileLockWarningCheck.Toggled += _ => WriteHardpointToSimBus(h);
            defenseGroup.AddChild(h.MissileLockWarningCheck);

            h.DecoyCountBox = MakeSpinBox(0, 99, 12);
            h.DecoyCountBox.ValueChanged += _ => WriteHardpointToSimBus(h);
            defenseGroup.AddChild(Labeled("Decoy Count", h.DecoyCountBox));

            root.AddChild(defenseGroup);

            root.AddChild(new HSeparator());
            _hardpoints.Add(h);

            RefreshModuleDropdown(h);
            UpdateHardpointControlVisibility(h);
        }

        return root;
    }

    // Shows/hides category-specific control groups based on the currently
    // selected category and module. Called on category/module change and
    // during live-mirror sync.
    private static void UpdateHardpointControlVisibility(HardpointState h)
    {
        string cat = h.CategoryOption.GetItemText(h.CategoryOption.Selected);
        string mod = h.ModuleOption.ItemCount > 0
            ? h.ModuleOption.GetItemText(h.ModuleOption.Selected) : "";

        h.UtilityGroup.Visible = cat == "utility_tool";
        h.CargoGroup.Visible   = cat == "cargo_storage";
        h.ReeferGroup.Visible  = mod == "Reefer Pod";
        h.SensorGroup.Visible  = cat == "sensor_ew";
        h.DefenseGroup.Visible = cat == "defense";
    }

    // Repopulates h.ModuleOption based on the currently selected category.
    private void RefreshModuleDropdown(HardpointState h)
    {
        string category = h.CategoryOption.GetItemText(h.CategoryOption.Selected);
        string prevName = h.ModuleOption.ItemCount > 0
            ? h.ModuleOption.GetItemText(h.ModuleOption.Selected) : "";

        _mirrorActive = true;
        h.ModuleOption.Clear();
        if (category == "empty")
        {
            h.ModuleOption.AddItem("(empty)");
            h.ModuleOption.Select(0);
            h.ModuleOption.Disabled = true;
        }
        else
        {
            h.ModuleOption.Disabled = false;
            int selectIdx = 0;
            int idx = 0;
            foreach (var (cat, name) in KnownModules)
            {
                if (cat != category) continue;
                h.ModuleOption.AddItem(name);
                if (name == prevName) selectIdx = idx;
                idx++;
            }
            h.ModuleOption.Select(selectIdx);
        }
        _mirrorActive = false;
    }

    // Writes all admin control state directly to SimBus.Hardpoints[slot-1],
    // then calls AdminUpdateHardpoint which publishes the module payload.
    // Category-specific fields are written before AdminUpdateHardpoint so
    // the final publish includes them.
    private void WriteHardpointToSimBus(HardpointState h)
    {
        if (SimBus.Instance == null) return;
        var hp = SimBus.Instance.Hardpoints[h.Slot - 1];

        string category = h.CategoryOption.GetItemText(h.CategoryOption.Selected);
        string? name = category == "empty" ? null
            : (h.ModuleOption.ItemCount > 0
                ? NullIfEmpty(h.ModuleOption.GetItemText(h.ModuleOption.Selected))
                : null);
        string modeRaw = h.ModeOption.GetItemText(h.ModeOption.Selected);
        string? mode = modeRaw == "—" ? null : modeRaw;
        bool? attached = category == "utility_tool" && name == "Grapple/Winch Rig"
            ? h.AttachedCheck.ButtonPressed : (bool?)null;

        // Cargo fields
        hp.FillPct  = (float)h.FillPctSlider.Value;
        hp.Contents = NullIfEmpty(h.ContentsEdit.Text);
        hp.TempC    = name == "Reefer Pod" ? (float?)(float)h.TempCBox.Value   : null;
        hp.TempMin  = name == "Reefer Pod" ? (float?)(float)h.TempMinBox.Value : null;
        hp.TempMax  = name == "Reefer Pod" ? (float?)(float)h.TempMaxBox.Value : null;

        // Sensor fields
        hp.ScannerModeActive = h.ScannerModeActiveCheck.ButtonPressed;
        hp.ScannerModeBeam   = h.ScannerModeBeamCheck.ButtonPressed;
        hp.StealthOn         = h.StealthOnCheck.ButtonPressed;

        // Defense fields
        hp.ShieldOn             = h.ShieldOnCheck.ButtonPressed;
        hp.ShieldSelectedFacing = h.ShieldFacingOption.GetItemText(h.ShieldFacingOption.Selected);
        foreach (var facing in new[] { "fore", "aft", "port", "starboard" })
            hp.ShieldStrengths[facing] = (float)h.ShieldStrengthSliders[facing].Value;
        hp.PdEngaged          = h.PdEngagedCheck.ButtonPressed;
        hp.MissileLockWarning = h.MissileLockWarningCheck.ButtonPressed;
        hp.DecoyCount         = (int)h.DecoyCountBox.Value;

        // Base fields + publish via established path
        SimBus.Instance.AdminUpdateHardpoint(
            h.Slot, category, name,
            h.ArmedCheck.ButtonPressed,
            h.ActiveCheck.ButtonPressed,
            (float)h.IntensitySlider.Value,
            mode, attached);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ── Live-mirror syncs (sim → admin panel, NoSignal path) ─────────────────

    private void SyncPropulsionFromBus()
    {
        if (SimBus.Instance == null) return;
        var p = SimBus.Instance.Propulsion;

        _propThrottleSlider.SetValueNoSignal(p.ThrottleInput);
        _propMixSlider.SetValueNoSignal(p.MixTarget);
        _propRcsToggle.SetPressedNoSignal(p.RcsEnabled);
        _propDampenerToggle.SetPressedNoSignal(p.DampenersEnabled);
        if (_propDampenerModeLabel.Text != p.DampenerMode) _propDampenerModeLabel.Text = p.DampenerMode;
        _propReverseToggle.SetPressedNoSignal(p.ReverseEnabled);
        _propEngineTempSlider.SetValueNoSignal(p.EngineTemp);
        _propVelocityLabel.Text = $"{p.Velocity:0.00} m/s";
        _propAltitudeLabel.Text = $"{p.AltitudeM:0} m";
        _propCollisionForceLabel.Text = $"{p.CollisionForceN:0.0} N";

        if (_propSoiLabel.Text != p.SoiBody) _propSoiLabel.Text = p.SoiBody;

        // Planet section: gravity slider is admin-write (NoSignal so the
        // sim→UI→sim loop can't fire); radius and distance are read-only.
        var planet = SimBus.Instance.Planet;
        if (planet != null)
        {
            _planetGravitySlider.SetValueNoSignal(planet.SurfaceGravity);
            _planetRadiusLabel.Text = $"{planet.PlanetRadius:0} m";
            _planetAltitudeLabel.Text = $"{p.AltitudeM:0} m";
        }
    }

    private void SyncFtlFromBus()
    {
        if (SimBus.Instance == null) return;
        var ftl = SimBus.Instance.Ftl;

        _ftlArmToggle.SetPressedNoSignal(ftl.Armed);

        _ftlProgressSlider.SetValueNoSignal(ftl.Progress);

        int destIdx = ftl.DestinationIndex;
        if (_ftlDestinationDropdown.Selected != destIdx)
        {
            _mirrorActive = true;
            _ftlDestinationDropdown.Select(destIdx);
            _mirrorActive = false;
        }

        _ftlRangeSlider.SetValueNoSignal(ftl.Armed ? ftl.RangeAu : 0f);
        _ftlSignalLagSlider.SetValueNoSignal(ftl.SignalLagS);
        _ftlPowerField.SetValueNoSignal(ftl.Phase == FtlPhase.Idle ? 0 : 340);
    }

    private void SyncAlertsFromBus()
    {
        if (SimBus.Instance == null) return;
        var alerts = SimBus.Instance.Alerts;

        var overheat = alerts.Active.FirstOrDefault(a => a.Id == "alert_engines_overheat");
        _alertOverheatToggle.SetPressedNoSignal(overheat != null);
        _alertOverheatAckedLabel.Text = overheat?.Acknowledged == true ? "(acked)" : "";

        var ftlAbort = alerts.Active.FirstOrDefault(a => a.Id == "alert_ftl_aborted");
        _alertFtlAbortedToggle.SetPressedNoSignal(ftlAbort != null);
        _alertFtlAbortedAckedLabel.Text = ftlAbort?.Acknowledged == true ? "(acked)" : "";

        var atmo = alerts.Active.FirstOrDefault(a => a.Id == "alert_atmo_dampeners_inop");
        _alertAtmoDampenersToggle.SetPressedNoSignal(atmo != null);
        _alertAtmoDampenersAckedLabel.Text = atmo?.Acknowledged == true ? "(acked)" : "";

        var collision = alerts.Active.FirstOrDefault(a => a.Id == "alert_collision");
        _alertCollisionToggle.SetPressedNoSignal(collision != null);
        _alertCollisionAckedLabel.Text = collision?.Acknowledged == true ? "(acked)" : "";
    }

    private void SyncHardpointsFromBus()
    {
        if (SimBus.Instance == null) return;
        for (int i = 0; i < 4; i++)
        {
            var hp = SimBus.Instance.Hardpoints[i];
            var h  = _hardpoints[i];

            // Category
            int catIdx = Array.IndexOf(HardpointCategories, hp.Category);
            if (catIdx < 0) catIdx = 0;
            if (h.CategoryOption.Selected != catIdx)
            {
                _mirrorActive = true;
                h.CategoryOption.Select(catIdx);
                RefreshModuleDropdown(h);
                _mirrorActive = false;
            }

            // Module name: find in current dropdown items
            if (hp.Name != null)
            {
                for (int mi = 0; mi < h.ModuleOption.ItemCount; mi++)
                {
                    if (h.ModuleOption.GetItemText(mi) != hp.Name) continue;
                    if (h.ModuleOption.Selected != mi)
                    {
                        _mirrorActive = true;
                        h.ModuleOption.Select(mi);
                        _mirrorActive = false;
                    }
                    break;
                }
            }

            // Base fields
            h.ArmedCheck.SetPressedNoSignal(hp.Armed);
            h.ActiveCheck.SetPressedNoSignal(hp.Active);
            h.IntensitySlider.SetValueNoSignal(hp.Intensity);

            // Utility group
            string modeStr = hp.Mode ?? "—";
            for (int mi = 0; mi < h.ModeOption.ItemCount; mi++)
            {
                if (h.ModeOption.GetItemText(mi) != modeStr) continue;
                if (h.ModeOption.Selected != mi)
                {
                    _mirrorActive = true;
                    h.ModeOption.Select(mi);
                    _mirrorActive = false;
                }
                break;
            }
            h.AttachedCheck.SetPressedNoSignal(hp.Attached ?? false);

            // Cargo group
            h.FillPctSlider.SetValueNoSignal(hp.FillPct);
            string newContents = hp.Contents ?? "";
            if (h.ContentsEdit.Text != newContents)
            {
                _mirrorActive = true;
                h.ContentsEdit.Text = newContents;
                _mirrorActive = false;
            }
            h.TempCBox.SetValueNoSignal(hp.TempC   ?? 4);
            h.TempMinBox.SetValueNoSignal(hp.TempMin ?? 2);
            h.TempMaxBox.SetValueNoSignal(hp.TempMax ?? 8);

            // Sensor group
            h.ScannerModeActiveCheck.SetPressedNoSignal(hp.ScannerModeActive);
            h.ScannerModeBeamCheck.SetPressedNoSignal(hp.ScannerModeBeam);
            h.StealthOnCheck.SetPressedNoSignal(hp.StealthOn);

            // Defense group
            h.ShieldOnCheck.SetPressedNoSignal(hp.ShieldOn);
            int facingIdx = hp.ShieldSelectedFacing switch
            {
                "fore" => 0, "aft" => 1, "port" => 2, "starboard" => 3, _ => 0,
            };
            if (h.ShieldFacingOption.Selected != facingIdx)
            {
                _mirrorActive = true;
                h.ShieldFacingOption.Select(facingIdx);
                _mirrorActive = false;
            }
            foreach (var facing in new[] { "fore", "aft", "port", "starboard" })
                h.ShieldStrengthSliders[facing].SetValueNoSignal(hp.ShieldStrengths[facing]);
            h.PdEngagedCheck.SetPressedNoSignal(hp.PdEngaged);
            h.MissileLockWarningCheck.SetPressedNoSignal(hp.MissileLockWarning);
            h.DecoyCountBox.SetValueNoSignal(hp.DecoyCount);

            UpdateHardpointControlVisibility(h);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string? NullIfEmpty(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
