using Godot;

namespace ColdOrbit.SimCore;

// Runtime-built SoI for any destination without a hand-crafted scene.
// SceneManager constructs this node, attaches visual children, then calls
// SetPlanet() before adding it to the tree.
public partial class GenericSoI : BaseSoI
{
    private Planet _planet;

    public void SetPlanet(Planet planet) => _planet = planet;

    public override void OnPlayerEntered()
    {
        SimBus.Instance.Planet = _planet;
    }

    public override void OnPlayerExited()
    {
        SimBus.Instance.Planet = null;
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
    }
}
