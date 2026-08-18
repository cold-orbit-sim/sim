using System.Collections.Generic;
using Godot;

namespace ColdOrbit.SimCore;

public partial class SceneManager : Node
{
    public static SceneManager Instance { get; private set; }

    private static readonly Dictionary<string, string> SoiScenes = new()
    {
        { "kael",         "res://scenes/soi_kael.tscn" },
        { "kerath_star",  "res://scenes/soi_kerath_star.tscn" },
    };

    private BaseSoI _currentSoI;
    public BaseSoI CurrentSoI => _currentSoI;

    public override void _Ready()
    {
        Instance = this;
        CallDeferred(nameof(LoadStartingSoI));
    }

    private void LoadStartingSoI()
    {
        LoadSoI("kael", Vector3.Zero);
    }

    public void LoadSoI(string soiKey, Vector3 inheritedVelocity)
    {
        if (!SoiScenes.TryGetValue(soiKey, out var scenePath))
        {
            GD.PrintErr($"SceneManager: unknown SoI key '{soiKey}', falling back to kael");
            scenePath = SoiScenes["kael"];
        }

        if (_currentSoI != null)
        {
            _currentSoI.OnPlayerExited();
            _currentSoI.QueueFree();
            _currentSoI = null;
            SimBus.Instance.Planet = null;
        }

        var packed = GD.Load<PackedScene>(scenePath);
        var soi = packed.Instantiate<BaseSoI>();
        GetTree().Root.AddChild(soi);
        _currentSoI = soi;

        var ship = SimBus.Instance.PlayerShipNode;
        if (ship != null)
        {
            ship.Position = soi.SpawnPosition;
            ship.LinearVelocity = inheritedVelocity;
            ship.AngularVelocity = Vector3.Zero;
        }

        soi.OnPlayerEntered();

        SimBus.Instance.Propulsion.SoiBody = soi.SoiBodyName;
    }
}
