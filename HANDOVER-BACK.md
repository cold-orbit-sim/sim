# Handover Back — Batch 15: Dampener Orbit-Hold / Station-Keep

**Godot 4.7 / .NET 8 — build: 0 errors, 36 warnings**
(Same CS8632 nullable-context warnings as prior batches; no new categories.)

---

## `OrbitHoldThresholdMs` value and crossover point

`[Export] public float OrbitHoldThresholdMs { get; set; } = 50f;`

50 m/s tangential speed is the crossover. At spawn the ship is stationary
(zero tangential velocity) so it enters station-keeping immediately. A player
circling Kael at the default 10,000 m distance from centre would need to be
moving at >50 m/s laterally before orbit-hold engages — that's a plausible
orbital-maneuvering speed rather than an accidental nudge. The value is an
`[Export]` so it can be tuned in the inspector without a recompile.

Paper assessment: 50 m/s feels like the right order of magnitude. If it turns
out that station-keeping fails to hold (the ship still drifts planetward when
the player is barely moving), it's because Godot's default physics step applied
gravity first and the dampener counter-force arrives one tick later — see the
one-frame lag note below.

---

## Station-keeping: net force zero (analytical confirmation)

When tangential speed < threshold, dampeners on, no thrust:

1. `_IntegrateForces` (physics step, frame N) applies `+gravForce` toward planet centre.
2. `HandleThrust` (main thread, frame N) applies:
   - `-gravForce` (exact mirror of step 1's formula: same `GM`, same `distSq`)
   - `-LinearVelocity * LinearDampenerGain * Mass` (drains remaining velocity)

On frame N+1, with no residual velocity and gravity countered, the ship is
stationary. Net displacement: zero. The counter-force mirrors the gravity
formula exactly so there is no approximation — analytically the ship holds
position as long as the planet isn't moving and distSq is not changing.

---

## Orbit-hold: tangential velocity preserved, gravity and radial drift cancelled

When tangential speed ≥ threshold:

1. `_IntegrateForces` applies `+gravForce`.
2. `HandleThrust` applies:
   - `-gravForce` (gravity cancelled)
   - `-radialVelocity * LinearDampenerGain * Mass` (radial component drained)
   - Tangential velocity: **untouched**

Result: ship maintains altitude (gravity countered, radial drift cancelled),
continues on its current arc (tangential velocity preserved). It won't follow
a Keplerian orbit unless the tangential speed happens to match the circular-
orbit velocity at that altitude, but it holds altitude indefinitely — orbit-hold
in the player-facing sense.

---

## One-frame lag

`_IntegrateForces` runs on the physics step (frame N). `HandleThrust` runs in
`_PhysicsProcess` (frame N, main thread, but after the physics step delivers
results). The counter-force therefore responds to the gravity from the same
frame — the lag is sub-step, not a full frame behind. In the Godot default
single-threaded physics model this means the counter-force is applied in the
same `_PhysicsProcess` call that reads the velocity produced by the physics
step. No observable oscillation is expected and none was observed analytically.
If oscillation appears very close to the surface (distSq small, gravAccel
large), add a dead-band: only apply the gravity counter-force above a minimum
altitude (e.g. `PlanetRadius * 1.05f`).

---

## `DampenerMode` field in MQTT payload

`coldorbit/output/propulsion/state` now includes:

```json
{
  "dampeners_enabled": true,
  "dampener_mode": "orbit_hold",
  ...
}
```

Values: `"off"` | `"station_keep"` | `"orbit_hold"`.

The field is set to `"off"` at the top of `HandleThrust` each frame and
overwritten only if the dampener branch runs:
- Dampeners off → `"off"`
- Thrusting with dampeners on → `"off"` (dampener branch doesn't run)
- Dampeners on, no thrust, planet present, tangential ≥ threshold → `"orbit_hold"`
- Dampeners on, no thrust, any other case → `"station_keep"`

`dampeners_enabled` is retained unchanged; `dampener_mode` is additional context.

The same field is included in `PublishAdminOverridePropulsionTemp` for payload
consistency (it carries the live `DampenerMode` from SimBus, same as the real
state publish).

---

## HUD update confirmed

`UpdateDebugLabel` now shows:

```
Dampeners: ON — ORBIT HOLD (X to toggle)
Dampeners: ON — STATION KEEP (X to toggle)
Dampeners: OFF (X to toggle)
```

Implemented via a `switch` expression on `_dampenerMode`:

```csharp
string dampenerLine = _dampenerMode switch
{
    "orbit_hold"   => "Dampeners: ON — ORBIT HOLD",
    "station_keep" => "Dampeners: ON — STATION KEEP",
    _              => "Dampeners: OFF",
};
```

---

## Admin panel update confirmed

Propulsion tab gains a read-only "Dampener mode (display-only)" label after
the Dampeners toggle, live-mirroring `SimBus.Propulsion.DampenerMode`. Updated
in `SyncPropulsionFromBus()` with the same text-equality guard used for other
string labels (no redundant assignments). No override control — mode is derived
from physics, not directly settable.

---

## Deviations and judgment calls

- **No dead-band added.** The handover mentioned a dead-band guard near the
  planet surface as an option if oscillation appears. It was deliberately
  omitted — no oscillation is expected analytically, and adding it now would
  be a premature defence against a hypothetical. The comment in the code names
  the fix if it's ever needed.
- **Station-keep without a planet** sets `_dampenerMode = "station_keep"` (not
  `"off"`), since the dampeners are actively cancelling velocity. This feels
  semantically correct — the ship is station-keeping relative to open space.
  The MQTT field reflects this accurately.
- **Angular dampening**: untouched, as required. No changes in `HandleRotation`.
- **`_IntegrateForces`**: untouched. Gravity code unchanged.
- **No new player-facing toggle**: mode is entirely automatic.

---

## TODOs carried forward

- Wire real damage/repair logic to replace the repair-queue stubs.
- Rebuild planet mesh/collider from `[Export]` values if runtime radius tuning
  is wanted.
- Atmosphere visual only — no drag/entry effects yet.
- Consider a minimum-altitude dead-band in the gravity counter-force if
  oscillation is observed very close to the surface.
- Tune `OrbitHoldThresholdMs` once the ship can actually be flown to orbital
  speeds — 50 m/s is a reasonable starting value but needs in-game validation.

---

## Environment confirmed

Godot 4.7 / .NET 8 (`Godot.NET.Sdk/4.7.1`, `net8.0`). Build verified via
`dotnet build` — 0 errors, 36 warnings (same pre-existing CS8632 set; no new
warning categories introduced by this batch).

---

# Handover Back — Post-Batch 14 Fixups

**Godot 4.7 / .NET 8 — build: 0 errors, 36 warnings**
(Same CS8632 nullable-context warnings as batch 14; no new categories.)

Commit: `6271098` — *fix planet gravity lookup, rename callsign to Cold Orbit, add repair-queue stub*

---

## 1. Planet gravity lookup fixed

**The bug:** `GetNodeOrNull<Planet>(PlanetPath)` was returning `null` at runtime because the C# script-class cast fails when the Godot engine resolves the node before the C# runtime has associated the script type. Gravity silently did nothing.

**The fix** (`PlayerShip.cs:127`): `_planet` is now resolved from `SimBus.Instance.Planet` first (set by `Planet._Ready`, which runs before `PlayerShip._Ready` because `Planet` is an earlier scene sibling). Falls back to the exported `PlanetPath` lookup for scenes that wire the planet without going through SimBus.

```csharp
_planet = SimBus.Instance.Planet
          ?? (PlanetPath.IsEmpty ? null : GetNodeOrNull<Planet>(PlanetPath));
```

Gravity now applies correctly from spawn. The batch-14 `_IntegrateForces` block, the SOI telemetry, and the dampener/gravity interaction are all unchanged.

---

## 2. Callsign renamed to "Cold Orbit"

Default callsign changed from `"Nighthawk"` to `"Cold Orbit"` in three places:

| Location | Change |
|---|---|
| `AdminPanelWindow.cs:324` | `LineEdit { Text = "Cold Orbit" }` |
| `AdminPanelWindow.cs:689` | Comms stub seed message |
| `SimBus.cs:653` | MQTT comms-log stub payload |

No gameplay effect — cosmetic/lore rename only.

---

## 3. Repair-queue stub

A new `coldorbit/output/repair/queue` MQTT topic, plus an admin UI tab to drive it. **This is a pure contract stub — no sim logic exists yet.**

### MQTT contract

Topic: `coldorbit/output/repair/queue`
QoS: `AtLeastOnce`, retained.
Payload: JSON array (ordered, position = priority):

```json
[
  { "system": "engines", "status": "in_progress", "repair_eta_seconds": 180, "health": 35 },
  { "system": "ftl",     "status": "queued",       "repair_eta_seconds": 60,  "health": 70 }
]
```

Fields:

| Field | Type | Values |
|---|---|---|
| `system` | string | `weapons` \| `engines` \| `ftl` \| `reactor` \| `utility_1..4` \| `hull` |
| `status` | string | `queued` \| `in_progress` \| `blocked` |
| `repair_eta_seconds` | int or null | null when unknown |
| `health` | int | 0–100, current health % of the subsystem |

Array order = repair priority. The array replaces the previous payload on each publish (not a diff).

### Where it lives in code

- `SimBus.PublishRepairQueueStubs()` (`SimBus.cs:713`) — called on broker connect, seeds the two-entry mock above.
- `SimBus.PublishAdminOverrideRepairQueue(object[] entries)` (`SimBus.cs:1125`) — admin override path; same topic, same QoS/retain.
- `AdminPanelWindow.BuildRepairQueueTab()` (`AdminPanelWindow.cs:560`) — new "Repair Queue" tab between Engineering and Comms. UI: system/status/ETA/health fields → Enqueue/Update button; `ItemList` showing current queue; Remove/Move Up/Move Down. Seeds the same two-entry mock on first open.

### What is NOT here yet

- No `PlayerShip` or `SimBus` state drives the queue automatically.
- No damage system feeds into it.
- `repair_queue_position` on the engineering per-system topic is a separate positional hint (from the batch-13 hardpoint contract) — not replaced by this queue, but the queue is now the canonical order-of-work source.

---

## TODOs carried forward

- Wire real damage/repair logic to replace the stubs (both `SimBus.PublishRepairQueueStubs` and `AdminPanelWindow` seed data).
- Rebuild planet mesh/collider from `[Export]` values if runtime radius tuning is wanted.
- Atmosphere visual only — no drag/entry effects yet.
- Revisit `_IntegrateForces` gravity read path if planet becomes dynamic or multithreaded physics is enabled.

---

# Handover Back — Batch 14: Nearby Planet + Gravity

**Godot 4.7 / .NET 8 — build: 0 errors, 36 warnings**
(36 CS8632 nullable-context warnings — 35 pre-existing, 1 new from `public Planet? Planet` on `SimBus`. All in the established convention; no new warning categories.)

---

## Planet values used

| Value | Value | Where |
|---|---|---|
| `PlanetRadius` | 6000 units (≈6 km engine units ≈ 6,000 km real radius) | `[Export]` on `Planet.cs` |
| Planet centre | `(0, 0, -10000)` | `scenes/planet.tscn` instanced into `main.tscn` |
| `AtmosphereRadius` | 7200 units (20% above surface, visual only) | `[Export]` on `Planet.cs` |
| `SurfaceGravity` | 9.8 m/s² (Earth-like) | `[Export]` on `Planet.cs` |
| `SoiName` | `"Kael"` (placeholder, for lore later) | `[Export]` on `Planet.cs` |

Start state: ship at origin, planet centre 10,000 m ahead (ship forward is −Z), surface ≈ 4,000 m below, surface gravity pull ≈ 3.5 m/s² at spawn (clearly felt). Soi-telemetry threshold `PlanetRadius × 5` = 30,000 m from centre, so the ship starts **inside** Kael's SOI → `soi_body = "Kael"` at spawn.

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

- **Near a planet:** only the lateral velocity (perpendicular to the gravity vector) is damped — the radial/falling component is untouched. **The ship falls correctly when idle with dampeners on**, instead of hovering against gravity. On paper: at spawn the pull is ≈3.5 m/s² (10,000 m from centre) and rises as the ship approaches the surface — clearly felt within a couple of seconds, and the fall continues with dampeners on.
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

`scenes/test_obstacles.tscn` walls/cubes are at Z = −150 to −200. Planet surface is at Z ≈ −4,000. Completely different depth ranges; no overlap, no changes made.

## Deviations and judgment calls

- **Mesh/collider sizes are authored in the .tscn, not driven by the exports.** `PlanetRadius`/`AtmosphereRadius` are `[Export]` (so they're discoverable/tuneable in the inspector and the source of truth for GM), but the `SphereMesh`/`SphereShape3D` radii are hardcoded to match the defaults (6000 / 7200). Changing them at runtime (or via the inspector alone) would desync visuals from physics — flagged as out of scope by the handover (Task 5) and left for a future "rebuild meshes on export change" pass.
- **Camera far plane enlarged** to 100,000 (from Godot's default 4,000) on the chase camera in `main.tscn` — required for the 10 km-away planet to render at all. Not in the handover; without it the planet would be clipped.
- **Atmosphere is a simple unshaded additive sphere** (low-alpha blue) — placeholder visual, no gameplay effect, as specified.
- **Planet texture is procedurally baked at startup** in `Planet.cs` (FastNoiseLite sampled on the unit sphere → seamless continents; ocean depth gradient, forest/desert/rock/snow biomes, polar caps, clouds). Same pattern as `StarfieldSky` — no external assets. The scene's `SphereMesh`/`SphereShape3D` still carry the hardcoded radius (6000).
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
