using Godot;

namespace ColdOrbit.SimCore;

public partial class SoiKerathStar : BaseSoI
{
    public override void OnPlayerEntered()
    {
        SimBus.Instance.Planet = null; // no planetary gravity in star SoI
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
    }

    public override void OnPlayerExited()
    {
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
    }
}
