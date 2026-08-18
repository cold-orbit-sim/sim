using Godot;

namespace ColdOrbit.SimCore;

public partial class SoiKael : BaseSoI
{
    [Export] public NodePath PlanetPath { get; set; }

    public override void OnPlayerEntered()
    {
        var planet = GetNodeOrNull<Planet>(PlanetPath);
        if (planet != null)
            SimBus.Instance.Planet = planet;
    }

    public override void OnPlayerExited()
    {
        SimBus.Instance.Planet = null;
    }
}
