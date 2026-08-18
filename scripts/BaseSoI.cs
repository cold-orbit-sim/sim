using Godot;

namespace ColdOrbit.SimCore;

public abstract partial class BaseSoI : Node3D
{
    [Export] public Vector3 SpawnPosition { get; set; } = Vector3.Zero;
    [Export] public string SoiBodyName { get; set; } = "Unknown";

    public virtual void OnPlayerEntered() { }
    public virtual void OnPlayerExited() { }
}
