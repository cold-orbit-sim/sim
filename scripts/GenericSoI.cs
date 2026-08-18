using Godot;

namespace ColdOrbit.SimCore;

// Runtime-built SoI for any destination without a hand-crafted scene.
// SceneManager constructs this node, attaches visual children, then calls
// SetPlanet() before adding it to the tree.
public partial class GenericSoI : BaseSoI
{
    private Planet _planet;
    private Star _star;

    public void SetPlanet(Planet planet) => _planet = planet;
    public void SetStar(Star star) => _star = star;

    public override void OnPlayerEntered()
    {
        SimBus.Instance.Planet = _planet;
        SimBus.Instance.StarNode = _star;
    }

    public override void OnPlayerExited()
    {
        SimBus.Instance.Planet = null;
        SimBus.Instance.StarNode = null;
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
    }
}
