# Handover Back — Batch 14: Nearby Planet + Gravity

**Godot 4.7 / .NET 8 — build: 0 errors, 36 warnings**
(36 CS8632 nullable-context warnings — 35 pre-existing, 1 new from `public Planet? Planet` on `SimBus`. All in the established convention; no new warning categories.)

---

## Planet values used

| Value | Value | Where |
|---|---|---|
| `PlanetRadius` | 6000 units (≈6 km engine units ≈ 6,000 km real radius) | `[Export]` on `Planet.cs` |
| Planet centre | `(0, 0, -20000)` | `scenes/planet.tscn` instanced into `main.tscn` |
| `AtmosphereRadius` | 7200 units (20% above surface, visual only) | `[Export]` on `Planet.cs` |
| `SurfaceGravity` | 9.8 m/s² (Earth-like) | `[Export]` on `Planet.cs` |
| `SoiName` | `"Kael"` (placeholder, for lore later) | `[Export]` on `Planet.cs` |

Start state: ship at origin, planet centre 20,000 m ahead (ship forward is −Z), surface ≈ 14,000 m below. Soi-telemetry threshold `PlanetRadius × 5` = 30,000 m from centre, so the ship starts **inside** Kael's SOI → `soi_body = "Kael"` at spawn.

## GM derived, not hardcoded

`public float GM => SurfaceGravity * PlanetRadius * PlanetRadius;` — `GM = 9.8 × 6000² = 352,800,000 m³/s²`. No raw gravitational constant anywhere. `PlayerShip` reads `_planet.GM` each physics frame.

## Gravity applies in `_IntegrateForces` alongside collision

Confirmed. `_IntegrateForces` (PlayerShip) keeps the batch-11 collision-impulse loop unchanged and appends the inverse-square gravity block after it:

```csharp
if (_planet != null)
{
    Vector3 toCenter = _planet.GlobalPosition - state.Transform.Origin;
    float distSq = toCenter.LengthSquared();
    if (distSq > 0.01f)
    {
        float accel = _planet.GM / distSq;
        state.ApplyCentralForce(toCenter.Normalized() * accel * Mass);
    }
}
```

- `GravityScale = 0f` line in `_Ready` **removed**. The scene node keeps `gravity_scale = 0.0` so Godot's built-in 9.8 m/s² "down" doesn't stack on the manual model.
- `PlanetPath` is `[Export] NodePath`, wired in `main.tscn` as `../Planet`; `_planet` cached in `_Ready`.
- Threading flag added (both in `Planet.cs` and the `_IntegrateForces` block): reads of `GM`/`GlobalPosition` are safe while the planet is a non-moving `StaticBody3D` and Godot's default single-threaded physics applies. If the planet ever becomes dynamic, or multithreaded physics is enabled, the `SurfaceGravity` write (admin) vs read (physics step) needs proper sync.

## Dampener / gravity interaction (on paper)

In `HandleThrust`, when dampeners are on and there is no thrust input:

- **Near a planet:** only the lateral velocity (perpendicular to the gravity vector) is damped — the radial/falling component is untouched. **The ship falls correctly when idle with dampeners on**, instead of hovering against gravity. On paper: at spawn the ship hangs briefly then begins a slow fall (≈0.88 m/s² at 20,000 m from centre), accelerating as it approaches the surface.
- **Open space:** unchanged — full velocity-proportional brake.
- Angular dampening untouched (spin is always lateral). `gravity_scale = 0.0` is retained so the manual model is the only gravity.

## `soi_body` telemetry

`coldorbit/output/propulsion/state` now publishes the live value from `PlayerShip.GetSoiBody()` (stored on `PropulsionState.SoiBody`):
- within `PlanetRadius × 5` (30,000 m) of centre → `"Kael"`
- beyond → `"Deep Space"`
- no planet → `"Deep Space"`

Label-only — gravity itself has **no SOI cutoff** (infinite inverse square), per the handover guardrail. The admin panel's old editable "SOI Body (override)" field is now a read-only live-mirror of this value.

## Altitude field confirmed

- **Debug HUD** (`PlayerShip.UpdateDebugLabel`): added `Alt: XXXXX m` line. `AltitudeM = dist(centre) − PlanetRadius`, negative below the surface (crash state).
- **MQTT telemetry**: `altitude_m` published in `coldorbit/output/propulsion/state` alongside `velocity_ms` (both via `PropulsionState.AltitudeM`, written every physics frame).
- **Admin panel Propulsion tab**: read-only "Altitude" label live-mirrored from `SimBus.Propulsion.AltitudeM`, plus a new "── Planet ──" section:
  - `SurfaceGravity m/s² (0–20)` slider → `SimBus.AdminSetPlanetGravity(float)` → applied on the main thread in `SimBus._Process` via a pending field (`_pendingPlanetGravity`), not written directly from the UI callback. This is the deferred-write pattern the handover asked for — no threading issue observed, and it's documented.
  - `Planet Radius` read-only label (runtime radius change would need mesh/collider rebuilds — out of scope, per handover).
  - `Distance to surface` read-only label, live-mirrors `AltitudeM`.

## Test obstacles — no conflict (confirmed, not assumed)

`scenes/test_obstacles.tscn` walls/cubes are at Z = −150 to −200. Planet surface is at Z ≈ −14,000. Completely different depth ranges; no overlap, no changes made.

## Deviations and judgment calls

- **Mesh/collider sizes are authored in the .tscn, not driven by the exports.** `PlanetRadius`/`AtmosphereRadius` are `[Export]` (so they're discoverable/tuneable in the inspector and the source of truth for GM), but the `SphereMesh`/`SphereShape3D` radii are hardcoded to match the defaults (6000 / 7200). Changing them at runtime (or via the inspector alone) would desync visuals from physics — flagged as out of scope by the handover (Task 5) and left for a future "rebuild meshes on export change" pass.
- **Camera far plane enlarged** to 100,000 (from Godot's default 4,000) on the chase camera in `main.tscn` — required for the 20 km-away planet to render at all. Not in the handover; without it the planet would be clipped.
- **Atmosphere is a simple unshaded additive sphere** (low-alpha blue) — placeholder visual, no gameplay effect, as specified.
- **Admin engine-temp override** (`PublishAdminOverridePropulsionTemp`) now carries live `altitude_m` and the live `SoiBody` so the ≤1-tick override payload stays consistent with the real state publish. PlayerShip overwrites it on the next telemetry tick regardless.
- **`SoiBody` naming**: used `SoiBody` on `PropulsionState` (matches the codebase's PascalCase for bus fields; the MQTT key remains snake_case `soi_body`).
- **Planet registers itself on SimBus** (`SimBus.Instance.Planet = this` in `Planet._Ready`) so the admin panel reaches it without a scene reference.

## TODOs for future batches

- Rebuild mesh/collider (and reposition) from `[Export]` values if runtime planet-radius tuning is ever wanted.
- Atmosphere is visual-only: real atmospheric drag/entry effects are deliberately deferred.
- Revisit `_IntegrateForces` gravity read path if the planet ever becomes dynamic or multithreaded physics is enabled.
- Decide where the planet name ("Kael") lives once lore/star-system data exists; `SoiName` is currently a single `[Export]` string.

## Environment confirmed

Godot 4.7 / .NET 8 (`Godot.NET.Sdk/4.7.1`, `net8.0`). Build verified locally via `dotnet build` (0 errors; warnings are the established CS8632 set). MSTest discovery doesn't run in this headless environment (pre-existing; requires the Godot runtime) — the three existing tests only assert unchanged export defaults.
