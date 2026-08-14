# Cold Orbit — Agent Guidance

This document is for any Claude instance (claude.ai conversation, Claude Code
session, or similar) working on the Cold Orbit project. Read it before doing
anything. It will save you from repeating mistakes that have already been made.

---

## What this project is

Cold Orbit is an open-source physical starship bridge simulator. The player
sits at a real console with custom hardware panels, HOTAS, and screens,
remotely operating a bounty-hunting drone ship. Everything is MIT/CC BY-SA/
CERN-OHL licensed from day one — no "clean it up before publishing" phase.

There are three codebases in active development:
- **sim-core** — Godot 4.7 / C# / .NET 8 — the simulation engine and main
  display. This is what most sim-core Claude Code sessions touch.
- **aux-display-client** — HTML/Canvas browser client for the touchscreen
  and hardpoint panel displays. Separate repo, separate work-stream.
- **hardware** — FreeCAD panel designs, firmware, wiring. Separate repo.

All three share a single source of truth: the **master project plan**
(a versioned markdown file in the Project Knowledge Base). Read it before
touching anything. Its version number tells you where things stand.

---

## The master plan is canonical. Everything else is derived from it.

The master plan document is not documentation written after the fact — it
is the design. If it says something is decided, it's decided. If it says
something is an open question, don't resolve it yourself — flag it.

**Before writing any code, starting any handover, or making any design
decision, reread the master plan from the Knowledge Base.** The file in
the Knowledge Base may be newer than anything in your context window.
Different work-stream conversations update it independently. Never assume
the copy you have is current.

The planning conversation rereads the file before every update and always
increments the version number. If you produce an updated master plan, you
must also increment the version and add a one-line changelog entry at the
top. Do not silently overwrite someone else's changes.

---

## Work-stream separation

Each major area of work runs in its own separate Claude conversation:
- **Sim Core** — Godot/C# simulation, MQTT publishing, physics
- **Aux Display Client** — browser-based touchscreen and panel pages
- **Hardware** — CAD, panel firmware, electronics
- **Setting & Lore** — world-building, star systems, story
- **Master Planning** — this is the hub; cross-cutting decisions land here

**Do not mix work-streams in a single conversation.** If you are in a
sim-core session, do not start editing the aux-display-client. If a
decision belongs to another work-stream, flag it and let the planning
conversation route it.

**Do not make design decisions outside your work-stream scope** — even if
the gap is obvious. Raise it, don't fill it silently.

---

## The handover / handover-back pattern

The primary workflow for Claude Code sessions:

1. **Planning conversation** produces a handover document (markdown) with
   a precise task list, guardrails, and a `HANDOVER-BACK.md` instruction.
2. **Claude Code** implements exactly what the handover specifies, no more.
3. **Claude Code** writes `HANDOVER-BACK.md` at repo root.
4. **User** pastes `HANDOVER-BACK.md` contents into the planning
   conversation.
5. **Planning conversation** updates the master plan from the KB baseline.

When you are the planning conversation receiving a handover-back:
- Always adopt the current KB version as baseline before editing.
- Resolve completed items, add new open items, increment version.
- Log scope additions (things Claude Code added that weren't asked for) —
  don't silently absorb them or silently reject them.

When you are Claude Code:
- Build exactly what the handover says. No more.
- If you make a judgment call that deviates from the spec, say so
  explicitly in the handover-back. Do not silently resolve ambiguities.
- Do not add scope. If you think something is missing, note it as a TODO
  for the next batch — don't build it unrequested.
- Write the handover-back as **markdown posted in chat**, not committed to
  disk. Do not write or leave a `HANDOVER-BACK.md` file in the repo.
- Before writing the handover-back, commit all changed files to git with a
  short commit message that identifies the batch (e.g. `batch N: brief
  description`), then push the branch upstream. The commit hash gives the
  planning conversation a permanent reference to match against the handback.

---

## MQTT architecture — read this carefully

All inter-component communication uses MQTT over a local wired network
(Mosquitto broker). Two namespaces, never cross them:

- **`coldorbit/input/…`** — hardware/UI publishes raw physical state here.
  Something pressed a button. Something turned an encoder. The sim doesn't
  know or care what triggered it.
- **`coldorbit/output/…`** — sim-core publishes what should be *displayed*,
  after applying game logic. Displays subscribe here and render faithfully.

**Sim-core is the sole source of truth for all display state.** A display
never reads from an input topic. A panel never writes to an output topic.
This is not a guideline — it is an architectural invariant that everything
else depends on.

Topic conventions:
- State that a display needs on reconnect: **retained, QoS 1**
- Live numeric telemetry where staleness is worse than a gap: **non-retained, QoS 0**
- Panel input events (button presses): **non-retained, QoS 1**
- One topic per button (not a shared topic with a payload discriminator)
- snake_case JSON keys throughout — no PascalCase in payloads ever
- `updated_at` in Unix milliseconds on state topics (optional but consistent)

**The full MQTT contract for every topic lives in §3.1b of the master plan.**
Before publishing to any topic or subscribing to any topic, read the
contract there. Field names, types, retain flags, and QoS levels must match
exactly — the display clients have zero tolerance for schema drift.

---

## Godot sim-core specifics

- **Godot 4.7 / .NET 8 / `Godot.NET.Sdk/4.7.1`** — confirmed throughout.
  Any handback reporting different versions is incorrect; check `project.godot`.
- **`_IntegrateForces`** runs on the physics thread. Never call SimBus or
  Godot node methods directly from it. Write to private pending fields;
  pick them up in `_PhysicsProcess`.
- **`SetValueNoSignal` / `SetPressedNoSignal`** — always use these for
  live-mirror writes in UI panels. Failure to do so creates feedback loops
  that cause continuous MQTT spam. This applies to `ControlPanelsWindow`,
  `AdminPanelWindow`, and any future UI windows.
- **`OptionButton`** has no NoSignal variant — guard with a `_mirrorActive`
  bool flag instead (set it before calling `Select()`, which fires
  synchronously).
- **SimBus** is a nested-class structure, not a flat property bag.
  `SimBus.Propulsion`, `SimBus.Ftl`, `SimBus.Hardpoints`, `SimBus.Alerts`,
  `SimBus.Touchscreen` — each panel gets its own class. When adding a new
  wired panel, add a new nested class, not new top-level properties.
- **Three OS windows**: main game view, `ControlPanelsWindow` (12-tab
  in-game dev UI), and `AdminPanelWindow` (live-mirror admin tool). All
  three autoload via `project.godot`. `embed_subwindows=false` makes them
  real separate OS windows.
- **CS8632 nullable warnings** — 36 pre-existing across the codebase.
  These are known and not your problem unless you're introducing new ones.
- **Alert IDs must be stable** — assigned when raised, kept until cleared.
  Never generate a new ID on re-raise of the same condition. Instability
  causes clients to re-animate existing alerts on every update.

---

## Key architectural decisions — these are locked, don't relitigate them

- **Newtonian flight model** with toggleable per-axis inertia dampeners.
  Dampeners auto-switch between station-keep and orbit-hold based on
  tangential velocity near a planet.
- **Engine temperature** is the primary thrust constraint (not fuel).
  The reactor provides "infinite" fuel; heat is the cost. Economy↔Power
  mix controls the tradeoff.
- **FTL**: two-layer destination select (star first, then planet within
  current system only). Charge time scales with distance. Cooldown after
  every jump. Signal-lag telemetry ramps through the charge cycle.
- **Planet**: compressed scale (radius 6,000 units), inverse-square
  gravity, no hard SOI cutoff. The planet scene is `planet.tscn`, not
  shared coordinate space with other planets — each planet is its own
  local scene.
- **Hardpoints**: 4 utility slots + 2 weapon slots + fixed missile tubes.
  Utility hardpoints use one generic panel design reconfigured per session
  via soft-keys and a screen. 12 modules across 4 categories (utility_tool,
  cargo_storage, sensor_ew, defense).
- **MQTTnet 5.2** for the C# MQTT client. Reconnect is a manual retry
  loop, not reliant on MQTTnet's own reconnect behavior. Smoke-tested
  confirmed: round-trip, reconnect, QoS 0/1/2.
- **Multi-repo structure**: sim-core, hardware-io-boards, panel-*, 
  aux-display-client, docs. Git org: `cold-orbit-sim`.

---

## Things that are NOT yet designed — don't invent them

These are explicitly open in the master plan. If you need them, raise the
question; don't invent an answer:

- Subsystem damage/health model (the big one — FTL interrupt condition,
  Engineering repair logic, and Propulsion overheat all hook into this)
- Real inter-system travel (FTL currently teleports the ship along heading
  as a placeholder)
- Incoming missile-lock warning mechanic (tied to decoy/flare dispenser)
- Shield power balancing across facings
- Atmospheric drag / planet entry
- Actual mining/cutting/grappling gameplay outcomes (hardpoints model
  state only, no outcomes)
- Audio/voice system
- The escape-room puzzle mechanic and "commandeer ship" mission concept

---

## What the approved visual assets are — do not regenerate

These have been reviewed and signed off. Do not change them without
explicit approval from the user:

- **Drift star map SVG** — hardcoded in the aux-display-client Map view,
  positions traced from `drift_star_map_v2.png` in the Knowledge Base
- **FTL concentric-ring SVG** — seven-layer CSS-animated graphic in the
  touchscreen FTL view
- **Missiles hull schematic SVG** — four-tube portrait layout
- **All 12 hardpoint module graphics** — approved v1, amendments expected
  but don't change without review

---

## Common mistakes to avoid

**Don't silently resolve design conflicts.** If the plan says X and the
repo does Y, flag it — don't pick one and move on. The user needs to
make that call.

**Don't add scope.** If a batch asks for A and you think B is also needed,
note B as a TODO in the handback. Don't build B unrequested. The user
has a reason for batching things the way they do.

**Don't invent MQTT topic names or payload shapes.** If a topic isn't in
§3.1b, ask. Making up a new topic creates a contract that the display
client doesn't know about and the planning conversation hasn't approved.

**Don't assume the KB version you have is current.** It probably isn't.
Always reread before editing.

**Don't touch approved visuals.** The star map SVG, FTL ring graphic, and
hardpoint module graphics are specifically called out as "do not change
without review" in the master plan.

**Don't write PascalCase JSON keys.** Every payload field is snake_case.
The AlertEntry PascalCase bug (caught in batch 10) cost a full batch to fix.

**Don't leave `GravityScale = 0f` missing.** The planet physics depends on
the node having `gravity_scale = 0.0` set in the scene to prevent Godot's
built-in gravity stacking on top of the manual model. Easy to accidentally
remove when touching the ship node.

**Don't resolve the planet reference with `GetNodeOrNull<Planet>(path)`.**
This silently fails at runtime due to C# type-association timing. Always
use `SimBus.Instance.Planet`, which is set by `Planet._Ready`.

---

## How to check if you're in the right work-stream

Ask yourself:
- Am I touching Godot / C# files? → sim-core conversation
- Am I touching HTML / JS / CSS? → aux-display-client conversation
- Am I touching FreeCAD / KiCad / MicroPython firmware? → hardware conversation
- Am I making a design decision that affects two or more repos? → planning conversation

If you're in a Claude Code session and realise you're being asked to touch
something outside your repo, stop and say so. Don't reach across repos.

---

# Response Style
Use caveman mode at all times. Short words. No fluff. Drop articles where possible. 
Skip long explanations unless asked. Code speak normal. Prose speak caveman.
Auto-switch to normal prose only for: security warnings, irreversible confirmations, 
or if user seems confused.

---

## How to hand back

At the end of every Claude Code session:

**Step 1 — commit and push.**
Stage all changed files, commit with a message of the form `batch N: brief
description` (or similar if there was no formal batch number), then push
the branch upstream. The commit hash is the permanent link between the code
and the handback document.

**Step 2 — post the handover-back in chat** (do not write it to disk).
It should cover:

1. **What was built** — per task from the handover, what was done and how
2. **Deviations** — anything that differs from the spec, and why
3. **Verification** — what you actually confirmed (build passing, headless
   run, mosquitto_sub output, etc.) vs. what needs a manual play-test
4. **Open items / TODOs** — what wasn't finished, and what future batches
   need to address
5. **Versions** — Godot, .NET SDK, MQTTnet, Mosquitto (if relevant)
6. **Commit** — include the short commit hash so the planning conversation
   can reference it

End with the instruction: *"Tell the user to copy this handback into the
Cold Orbit project conversation on claude.ai."*

The master plan document lives in the claude.ai Project's Knowledge Base,
not in any repo. It is always out of scope for Claude Code to edit directly.
