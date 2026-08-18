using Godot;

namespace ColdOrbit.SimCore;

public partial class SceneManager : Node
{
    public static SceneManager Instance { get; private set; }

    private BaseSoI _currentSoI;
    public BaseSoI CurrentSoI => _currentSoI;

    public override void _Ready()
    {
        Instance = this;
        CallDeferred(nameof(LoadStartingSoI));
    }

    private void LoadStartingSoI()
    {
        // Kael is system K, planet index 0
        var kael = new DriftData.Destination("K", 0, "Kael");
        LoadSoI(kael, Vector3.Zero);
    }

    public void LoadSoI(DriftData.Destination dest, Vector3 inheritedVelocity)
    {
        if (_currentSoI != null)
        {
            _currentSoI.OnPlayerExited();
            _currentSoI.QueueFree();
            _currentSoI = null;
            SimBus.Instance.Planet = null;
        }

        var soi = BuildSoI(dest);
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

        // Orient ship toward the SoI body on arrival.
        if (ship != null)
        {
            Vector3? bodyPos = SimBus.Instance.Planet?.GlobalPosition
                            ?? SimBus.Instance.StarNode?.GlobalPosition;
            if (bodyPos.HasValue)
            {
                var toBody = (bodyPos.Value - ship.GlobalPosition).Normalized();
                if (toBody.LengthSquared() > 0.0001f)
                {
                    var up = Mathf.Abs(toBody.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                    ship.GlobalTransform = new Transform3D(Basis.LookingAt(toBody, up), ship.GlobalPosition);
                    ship.AngularVelocity = Vector3.Zero;
                }
            }
        }

        SimBus.Instance.Propulsion.SoiBody = soi.SoiBodyName;
        SimBus.Instance.Ftl.CurrentSystemId = dest.SystemId;
    }

    // Routes to a hand-crafted .tscn for home-system destinations, otherwise
    // builds a generic SoI at runtime from DriftData.
    private BaseSoI BuildSoI(DriftData.Destination dest)
    {
        if (dest.SystemId == "K")
        {
            if (dest.IsStar)
                return GD.Load<PackedScene>("res://scenes/soi_kerath_star.tscn").Instantiate<BaseSoI>();
            if (dest.Name == "Kael")
                return GD.Load<PackedScene>("res://scenes/soi_kael.tscn").Instantiate<BaseSoI>();
        }

        var system = DriftData.GetSystem(dest.SystemId);
        return dest.IsStar ? BuildGenericStarSoI(system) : BuildGenericPlanetSoI(dest, system);
    }

    private static BaseSoI BuildGenericStarSoI(DriftData.StarSystem system)
    {
        var (color, energy, radius) = StarTypeParams(system.StarType);

        var soi = new GenericSoI();
        soi.SoiBodyName = system.StarName;
        soi.SpawnPosition = new Vector3(0f, 0f, -(radius * 6f));

        var star = new Star();
        star.StarRadiusM = radius;
        star.StarEmissionColor = color;
        star.StarEmissionEnergy = energy;
        star.HeatZoneAltitudeM = radius * 2f;
        star.MaxHeatPerSecond = HeatForStarType(system.StarType);
        soi.AddChild(star);
        soi.SetStar(star);

        return soi;
    }

    private static BaseSoI BuildGenericPlanetSoI(DriftData.Destination dest, DriftData.StarSystem system)
    {
        var soi = new GenericSoI();
        soi.SoiBodyName = dest.Name;
        soi.SpawnPosition = new Vector3(0f, 0f, 0f);

        var planetScene = GD.Load<PackedScene>("res://scenes/planet.tscn");
        var planet = planetScene.Instantiate<Planet>();
        planet.SoiName = dest.Name;
        planet.Position = new Vector3(0f, 0f, -10000f);
        soi.AddChild(planet);
        soi.SetPlanet(planet);

        return soi;
    }

    // Star visual parameters by spectral type.
    // Radii are in Godot units (1 unit ≈ 1 km at the sim's compressed scale).
    // Emission energy is an artistic multiplier, not a physical quantity.
    private static (Color color, float energy, float radius) StarTypeParams(string starType)
    {
        return starType switch
        {
            "B-type"             => (new Color(0.80f, 0.88f, 1.00f), 18f, 100000f),
            "A-type"             => (new Color(0.95f, 0.97f, 1.00f), 14f,  60000f),
            "F-type"             => (new Color(1.00f, 0.98f, 0.88f), 12f,  55000f),
            "G-type"             => (new Color(1.00f, 0.94f, 0.70f), 10f,  50000f),
            "K-type"             => (new Color(1.00f, 0.78f, 0.38f), 10f,  50000f),
            "M-type"             => (new Color(1.00f, 0.40f, 0.18f),  8f,  35000f),
            "Red giant"          => (new Color(1.00f, 0.28f, 0.08f), 14f, 200000f),
            "White dwarf"        => (new Color(0.88f, 0.94f, 1.00f),  8f,   5000f),
            "Brown dwarf"        => (new Color(0.58f, 0.32f, 0.10f),  3f,  15000f),
            "Binary"             => (new Color(0.90f, 0.95f, 1.00f), 15f,  75000f),
            "Pulsar"             => (new Color(0.70f, 1.00f, 1.00f), 18f,   3000f),
            "Black hole remnant" => (new Color(0.10f, 0.04f, 0.20f),  1f,   1000f),
            _                    => (new Color(1.00f, 0.94f, 0.70f), 10f,  50000f),
        };
    }

    private static float HeatForStarType(string starType)
    {
        return starType switch
        {
            "B-type"             => 60f,
            "A-type"             => 45f,
            "F-type"             => 35f,
            "G-type"             => 30f,
            "K-type"             => 30f,
            "M-type"             => 20f,
            "Red giant"          => 50f,
            "White dwarf"        => 25f,
            "Brown dwarf"        =>  8f,
            "Binary"             => 50f,
            "Pulsar"             => 80f,
            "Black hole remnant" =>  5f,
            _                    => 30f,
        };
    }
}
