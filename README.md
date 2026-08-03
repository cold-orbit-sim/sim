# Cold Orbit — Sim Core

The Godot simulation core for [Cold Orbit](../README.md), a physical
starship bridge simulator. This repo is the flight/simulation half of the
project — the physical console panels (separate repos) will eventually
drive it over MQTT.

This first pass is deliberately minimal: one ship, Newtonian flight, empty
space. No hardpoints, no combat, no FTL, no MQTT yet — just proving out the
core flight feel before anything else gets layered on.

## Requirements

- **Godot 4.x with .NET/C# support** (sometimes called the "Mono" build) —
  the standard Godot download does *not* include C# support, so double
  check you've got the right one from godotengine.org/download.
- **.NET SDK** compatible with the project (targets `net8.0`) installed
  system-wide, so Godot can build the C# scripts.

This was scaffolded against the `Godot.NET.Sdk/4.3.0` project format. If
you're on a newer 4.x release, Godot will likely offer to update
`sim-core.csproj` and `project.godot` for you on first open — that's
expected and fine.

## Running it

1. Open Godot, "Import" this folder (point it at `project.godot`).
2. Let it build the C# solution (Project → Tools → C# → Create/Build, if it
   doesn't happen automatically).
3. Press Play (F5). It'll run `scenes/main.tscn`.

## Controls (placeholder keyboard bindings)

| Key | Action |
|---|---|
| W / S | Main thrust forward / reverse |
| A / D | Yaw left / right |
| Up / Down | Pitch up / down |
| Q / E | Roll left / right |
| X | Toggle inertia dampeners |

These are registered in code (`PlayerShip.cs`), not the Godot editor's
Input Map, so there's nothing to configure — they'll get replaced with
HOTAS/panel input (via MQTT) later without needing any project settings
changes.

## Flight model

Newtonian with toggleable inertia dampeners, per the current design
decision:

- **Thrusting or rotating:** pure `F = ma` — no artificial speed cap.
- **Idle, dampeners ON (default):** drift and spin are actively countered,
  proportional to current velocity — the ship settles rather than coasting
  forever.
- **Idle, dampeners OFF:** full Newtonian drift — momentum persists until
  you counter it yourself.

## Known simplifications (fine for now, worth revisiting)

- Angular dampening brakes the whole angular-velocity vector at once, not
  per-axis — rolling while idle-pitching will fight itself slightly.
- No RCS/strafe translation yet (only main-engine forward/reverse + rotation).
  Maps to the Propulsion panel's RCS toggle (master plan §7.7) once added.
- Ship is a placeholder capsule mesh — swap in the real model whenever it's
  ready.
- Dampener toggle is a keyboard key for testing; it should eventually be
  driven by the physical Propulsion panel control.

## License

MIT — see `LICENSE`.
