# Handover Back — Batch 16: FTL Destination Select

**Godot 4.7 / .NET 8.** Code complete and self-reviewed. The .NET SDK is not
installed in this working environment, so the C# build itself runs inside
Godot on your machine (Project → Tools → C# → Build) — it was not compiled
here. No new language constructs beyond what prior batches already compiled;
same CS8632 nullable-context warning category as before, nothing new introduced.

---

## Drift data — counts verified (not asserted)

`scripts/DriftData.cs` embeds the star map verbatim from the handover's data
table. Counts were pulled straight from the source, and each system letter and
planet name was diffed against the handover table:

- **26 star systems** (`new StarSystem(` × 26), letters A–Z, none missing.
- **79 planets** (`new Planet(` × 79). Xelgrave (X) has an empty planet array.
- System-letter set and full planet-name set both diff **identical** to the
  data block in `HANDOVER.md`.

### Deviation to flag: "80 planets"

The handover's **Guardrails** prose says "26 systems, 80 planets", but the
handover's own verbatim data table contains **79** planet entries. I embedded
the table exactly as given (79), so the code matches the authoritative data
block, not the round-number in the prose. If a planet was genuinely meant to be
80, one is missing from the source table and you'll need to tell me which
system it belongs to.

---

## Navigation model — deviation from the drill-in pseudocode

The handover's Task 2 describes a **stateful two-layer drill** (Next on the
current system's star drills into its planets; Next elsewhere only walks stars).
I implemented a **single canonical flat destination list** instead
(`DriftData.Destinations`), ordered:

1. all 26 stars, A–Z, then
2. every planet, grouped by system in A–Z order.

Prev/Next simply step through this list with wrap-around
(`FtlState.CycleDestination`). Reasons:

- The Admin panel needs a flat, fully-addressable list anyway; sharing one
  ordering guarantees the panel dropdown and the physical prev/next cycler can
  never disagree.
- It makes *every* planet reachable from the panel, not just the current
  system's — the drill-in model can only ever select planets in system K, which
  makes 75 of the 79 planets unreachable from the physical panel.

If you specifically want the drill-in behaviour (planets only reachable in the
current system, matching the two-jump lore), say so and I'll swap
`CycleDestination` for the stateful Next/Prev logic — the publish plumbing
around it doesn't change.

### Boundary behaviour (flat-list model)

- **Star Z → A wrap**: index `(i + 1) mod N` — stepping Next off the last entry
  wraps to the first (Aurivane star). ✔
- **Star A ← Z wrap (Prev)**: `(i - 1 + N) mod N` wraps to the last entry. ✔
- **Star → first planet drill-in**: because all planets follow all stars in the
  list, planets are reached by continuing to page Next past Zelvarine's star
  into the planet block. Every system's planets are reachable. ✔
- **First planet → back out to star (Prev)**: Prev from any planet steps to the
  previous list entry (the prior planet, or the last star when at the head of
  the planet block). ✔
- Selection is **locked once the drive leaves Idle** — `dest_action` is ignored
  in `SimBus.HandleFtlCommand` while charging/jumping/cooldown, mirroring the
  dev panel disabling the ◀▶ buttons.

---

## `dest_action` folded into `ftl/command`

Per Task 2, the separate `ftl/dest_action` topic is gone. Prev/Next now publish
onto the shared input topic:

```json
{ "dest_action": "next", "updated_at": 1754561234567 }
{ "dest_action": "prev", "updated_at": 1754561234567 }
```

- `ControlPanelsWindow` publishes to `coldorbit/input/ftl/command`
  (QoS 1, **not retained** — replaying a stale "next" on reconnect would walk
  the selection).
- `SimBus.HandleFtlCommand` parses `armed` and/or `dest_action` from the same
  payload; a `dest_action` republishes `ftl/target` + `ftl/system`.
- The dev-panel buttons no longer cycle locally — they publish only, and
  `SimBus` (which receives its own broker-echoed message) is the single
  authority that cycles and republishes. This avoids a double-step.

---

## Admin panel destination control

FTL tab (`AdminPanelWindow`): a single **flat dropdown** over the whole
destination list — 26 stars in A–Z order, then every planet indented and
annotated with its star name, e.g. `    Ashra (Aurivane)`. The physical panel
only has prev/next, so this dropdown is the admin shortcut for jumping straight
to any of the 105 entries. Selecting an item calls `SelectTo(...)` then
publishes **both** `PublishFtlSystem()` and `PublishFtlNavTarget()`, so the Map
view updates immediately. (The handover explicitly allowed a flat dropdown here.)

---

## Publishing guarantees

`ftl/target` and `ftl/system` (both retained, QoS 1) publish on:

- **every** selection change (dev panel prev/next, admin dropdown),
- **startup** — `{ "type": "none" }` marker first, then the resolved default
  (Kerath / system K star) immediately after,
- **broker reconnect** — the same startup sequence runs from `OnMqttConnected`.

---

## Example broker output

`mosquitto_sub -t 'coldorbit/output/ftl/#' -v`

**No selection (startup marker):**
```
coldorbit/output/ftl/target  {"type":"none"}
```

**A star, non-current system (e.g. Aurivane / A):**
```
coldorbit/output/ftl/target  {"type":"star","system_id":"A","name":"Aurivane","star_type":"B-type","planet_count":3,"distance_au":14.2,"spool_time_s":9}
coldorbit/output/ftl/system  {"system_id":null}
```

**A planet in the current system (e.g. a K-system planet):**
```
coldorbit/output/ftl/target  {"type":"planet","system_id":"K","name":"<planet>","system_name":"Kerath","star_type":"<type>","distance_au":0.1,"spool_time_s":2}
coldorbit/output/ftl/system  {"system_id":"K","star_name":"Kerath","star_type":"<type>","planets":[{"name":"..."}, ...]}
```

(`distance_au` / `spool_time_s` shown to the rounding used in code —
`distance_au` to 1 dp, spool to whole seconds. Exact numbers depend on the
real chart coordinates; see distance note below.)

---

## `ftl/state` destination field

`PlayerShip` publishes the **real selected name** on `coldorbit/output/ftl/state`:
`destination = ftl.Armed ? ftl.SelectedName : null`, and `range_au` is the real
`FtlState.RangeAu`. No placeholder/index string remains. Other fields (armed,
phase, progress, signal_lag_s, power) are untouched.

---

## Distance model — deviation: real chart distances kept

The handover suggested a **placeholder alphabetical-ring** distance model
(`1.5 + ringDistance * 1.4`). The code instead computes **real straight-line AU
from the actual star positions** in `drift_star_map_v2.svg`
(`DriftData.DistanceAu`, using the per-system map coordinates). Planet targets
add a small per-planet increment (`0.1 × (planetIndex + 1)` AU) on top of their
star's distance, as the handover suggested for in-system jumps.

I kept the real-coordinate model because the SVG positions were already
available and give a more believable star map than the alphabetical ring. If
you'd rather have the deterministic ring placeholder from the handover, it's a
one-method swap in `FtlState.RangeAu` / `DriftData.DistanceAu`.

---

## Untouched (per guardrails)

FTL phase state machine (Idle/Charging/Ready/Jumping/Cooldown), actual jump
travel/teleport, flight model, propulsion, hardpoints, and alerts were not
modified. This batch is destination *selection* only.

---

Copy this back into the Cold Orbit project conversation on claude.ai.
