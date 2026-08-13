using System.Collections.Generic;
using Godot;

namespace ColdOrbit.SimCore;

// Manages the 7 named Camera3D views for the Cameras panel (§7.3 batch 18).
// Lives as a Node3D child of PlayerShip so it moves with the ship.
//
// All camera switches must happen on the main thread; the MQTT background
// thread writes SimBus.Cameras.PendingView and this node picks it up in
// _Process, matching the pending-field pattern used throughout the codebase.
//
// F1-F7 are dev shortcuts only (bypass MQTT, write directly to SwitchTo).
// They will be removed when physical hardware arrives.
public partial class CameraController : Node3D
{
    [Export] public NodePath CameraForwardPath { get; set; } = new NodePath();
    [Export] public NodePath CameraAftPath { get; set; } = new NodePath();
    [Export] public NodePath CameraChasePath { get; set; } = new NodePath();
    [Export] public NodePath CameraDorsalPath { get; set; } = new NodePath();
    [Export] public NodePath CameraVentralPath { get; set; } = new NodePath();
    [Export] public NodePath CameraDockingPath { get; set; } = new NodePath();
    [Export] public NodePath CameraDamagePath { get; set; } = new NodePath();

    private readonly Dictionary<string, Camera3D> _cameras = new();
    private static readonly HashSet<string> InternalViews = new() { "forward", "aft" };
    private ShipMesh _shipMesh;

    public override void _Ready()
    {
        _cameras["forward"] = GetNodeOrNull<Camera3D>(CameraForwardPath);
        _cameras["aft"]     = GetNodeOrNull<Camera3D>(CameraAftPath);
        _cameras["chase"]   = GetNodeOrNull<Camera3D>(CameraChasePath);
        _cameras["dorsal"]  = GetNodeOrNull<Camera3D>(CameraDorsalPath);
        _cameras["ventral"] = GetNodeOrNull<Camera3D>(CameraVentralPath);
        _cameras["docking"] = GetNodeOrNull<Camera3D>(CameraDockingPath);
        _cameras["damage"]  = GetNodeOrNull<Camera3D>(CameraDamagePath);

        RegisterKeyAction("cam_forward",  Key.F1);
        RegisterKeyAction("cam_aft",      Key.F2);
        RegisterKeyAction("cam_chase",    Key.F3);
        RegisterKeyAction("cam_dorsal",   Key.F4);
        RegisterKeyAction("cam_ventral",  Key.F5);
        RegisterKeyAction("cam_docking",  Key.F6);
        RegisterKeyAction("cam_damage",   Key.F7);

        _shipMesh = GetParent().GetNodeOrNull<ShipMesh>("ShipMesh");

        SwitchTo("forward");
    }

    public override void _Process(double delta)
    {
        // Pick up pending view from MQTT background thread.
        string? pending = SimBus.Instance?.Cameras.PendingView;
        if (pending != null)
        {
            SimBus.Instance.Cameras.PendingView = null;
            SwitchTo(pending);
        }

        // F1-F7 dev shortcuts — bypass MQTT, write directly to SwitchTo.
        if      (Input.IsActionJustPressed("cam_forward"))  SwitchTo("forward");
        else if (Input.IsActionJustPressed("cam_aft"))      SwitchTo("aft");
        else if (Input.IsActionJustPressed("cam_chase"))    SwitchTo("chase");
        else if (Input.IsActionJustPressed("cam_dorsal"))   SwitchTo("dorsal");
        else if (Input.IsActionJustPressed("cam_ventral"))  SwitchTo("ventral");
        else if (Input.IsActionJustPressed("cam_docking"))  SwitchTo("docking");
        else if (Input.IsActionJustPressed("cam_damage"))   SwitchTo("damage");
    }

    public void SwitchTo(string view)
    {
        if (!_cameras.TryGetValue(view, out var cam) || cam == null)
        {
            GD.PrintErr($"CameraController: no Camera3D found for view '{view}'");
            return;
        }
        cam.MakeCurrent();
        _shipMesh?.SetHullVisible(!InternalViews.Contains(view));
        if (SimBus.Instance == null) return;
        SimBus.Instance.Cameras.ActiveView = view;
        SimBus.Instance.PublishCameraState();
    }

    private static void RegisterKeyAction(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
    }
}
