using Godot;

namespace ColdOrbit.SimCore;

// Static collision obstacles used to test physics response and the hull-impact
// alert path. Not gameplay content — remove or hide before shipping.
public partial class TestObstacles : Node3D
{
    [Export] public bool ShowTestObstacles { get; set; } = true;

    public override void _Ready()
    {
        Visible = ShowTestObstacles;
    }
}
