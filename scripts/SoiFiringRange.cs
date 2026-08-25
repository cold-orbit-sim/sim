using Godot;

namespace ColdOrbit.SimCore;

public partial class SoiFiringRange : BaseSoI
{
    public override void OnPlayerEntered()
    {
        SimBus.Instance.Planet = null; // no gravity source in this scene
        GD.Print("SoiFiringRange: DEBUG scene active — this is a test range, not a real SoI. " +
                 "See plan v87+ batch 23 notes before shipping a build with this as the default start.");
    }
}
