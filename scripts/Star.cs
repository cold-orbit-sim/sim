using Godot;

namespace ColdOrbit.SimCore;

public partial class Star : Node3D
{
    [Export] public float StarRadiusM { get; set; } = 50000f;
    [Export] public Color StarEmissionColor { get; set; } = new Color(1.0f, 0.78f, 0.38f);
    [Export] public float StarEmissionEnergy { get; set; } = 10f;

    [Export] public float HeatZoneAltitudeM { get; set; } = 100000f;
    [Export] public float MaxHeatPerSecond { get; set; } = 30f;

    private MeshInstance3D _mesh;

    public override void _Ready()
    {
        _mesh = new MeshInstance3D();
        var sphere = new SphereMesh();
        sphere.Radius = StarRadiusM;
        sphere.Height = StarRadiusM * 2f;
        _mesh.Mesh = sphere;

        var mat = new StandardMaterial3D();
        mat.EmissionEnabled = true;
        mat.Emission = StarEmissionColor;
        mat.EmissionEnergyMultiplier = StarEmissionEnergy;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _mesh.MaterialOverride = mat;

        AddChild(_mesh);
    }

    public override void _PhysicsProcess(double delta)
    {
        var ship = SimBus.Instance.PlayerShipNode;
        if (ship == null) return;

        float dist = GlobalPosition.DistanceTo(ship.GlobalPosition);
        float surfaceDist = dist - StarRadiusM;
        float heatZoneTop = HeatZoneAltitudeM;

        if (surfaceDist < heatZoneTop && surfaceDist > 0f)
        {
            float proximity = 1f - (surfaceDist / heatZoneTop);
            float heatRate = proximity * MaxHeatPerSecond;
            SimBus.Instance.Propulsion.ExternalHeatRate = heatRate;
        }
        else
        {
            SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
        }
    }
}
