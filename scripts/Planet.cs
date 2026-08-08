using Godot;

namespace ColdOrbit.SimCore;

// Compressed-scale planet for the batch 14 nearby-body work. 1 engine unit
// represents ~1 km of real planetary radius, so a 6000-unit sphere reads as
// a ~6000 km world while keeping all gameplay (orbital approach, surface
// proximity) well within Godot's float-precision budget (<~100k units).
//
// Gravity is infinite inverse-square -- no SOI cutoff. SurfaceGravity is the
// designer-facing knob; GM is derived from it so gravity stays correct at any
// distance without a raw gravitational parameter the designer has to guess.
//
// Threading: SurfaceGravity is written from the Godot main thread (admin
// panel via SimBus.AdminSetPlanetGravity, applied in SimBus._Process) and
// read on the physics step (PlayerShip._IntegrateForces via GM). This is
// safe today because the planet is a StaticBody3D that never moves and
// Godot's default physics runs single-threaded. If the planet ever becomes
// dynamic (moving/rotating), or multithreaded physics is enabled, the
// SurfaceGravity write and GM/GlobalPosition reads need proper sync.
public partial class Planet : StaticBody3D
{
    [Export] public float PlanetRadius { get; set; } = 6000f;
    [Export] public float AtmosphereRadius { get; set; } = 7200f;
    [Export] public float SurfaceGravity { get; set; } = 9.8f;
    [Export] public string SoiName { get; set; } = "Kael";

    public float GM => SurfaceGravity * PlanetRadius * PlanetRadius;

    public override void _Ready()
    {
        // Register with the sim bus so the admin panel can reach us (gravity
        // override) and read planet constants without holding a scene ref.
        if (SimBus.Instance != null)
        {
            SimBus.Instance.Planet = this;
        }
    }
}
