using Godot;

namespace ColdOrbit.SimCore;

public partial class SoiKerathStar : BaseSoI
{
    public override void OnPlayerEntered()
    {
        SimBus.Instance.Planet = null;
        SimBus.Instance.StarNode = GetNodeOrNull<Star>("Star");
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
    }

    public override void OnPlayerExited()
    {
        SimBus.Instance.StarNode = null;
        SimBus.Instance.Propulsion.ExternalHeatRate = 0f;
    }
}
