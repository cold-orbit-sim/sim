# Cold Orbit — Batch 8 Handover Back

Session: 2026-08-05  
Branch: main  
Build: `dotnet build sim-core.csproj` → **0 errors**, 11 CS8632 warnings (nullable
annotation style warnings, pre-existing pattern — project has no `<Nullable>enable</Nullable>`).

---

## Propulsion state (`coldorbit/output/propulsion/state`)

**v43 contract shape: confirmed compliant.**

Published retained, QoS 1, at `TelemetryPublishRateHz` (default 10 Hz) from
`PlayerShip.PublishPropulsionState()`.

### New payload shape

```json
{
  "armed": false,
  "throttle": 0.0,
  "mix": 0.0,
  "rcs_enabled": true,
  "dampeners_enabled": true,
  "reverse_enabled": false,
  "engines": [
    { "id": "port",      "power_kw": 0, "temp_c": 0 },
    { "id": "centre",    "power_kw": 0, "temp_c": 0 },
    { "id": "starboard", "power_kw": 0, "temp_c": 0 }
  ],
  "velocity_ms": 0.0,
  "acceleration_ms2": 0.0,
  "soi_body": "Unknown"
}
```

Old payload fields removed: `overheated` (now in alerts), `updated_at`.

### Field reality vs simplification

| Field | Reality |
|---|---|
| `armed` | **Placeholder `false`** — no propulsion Arm state in sim yet |
| `throttle` | Real: abs of keyboard thrust input (0 or 1 from binary W/S keys) |
| `mix` | Real: interpolated propellant mix |
| `rcs_enabled`, `dampeners_enabled` | Real |
| `reverse_enabled` | Real: `true` while `thrust_reverse` key (S) is held |
| `engines[].power_kw` | Simplified: all three share the same value (`throttle × PowerPerEnginekW`, export default 1500 kW, so 0–1500 kW per engine) |
| `engines[].temp_c` | Simplified: all three share the single `_engineTemp` value (cast int °C) |
| `velocity_ms` | Real: `LinearVelocity.Length()` |
| `acceleration_ms2` | Real: velocity delta / dt, can go negative when decelerating |
| `soi_body` | **Hardcoded `"Unknown"`** — no gravity/SOI model |

### Publish cadence change

Previously on-change every physics frame. Now rate-limited to 10 Hz because the new
continuous fields (velocity, temp, accel) change every frame and would flood the broker
at ~60 publishes/sec. Old `_mqttLast*` tracking fields are removed.

---

## FTL state (`coldorbit/output/ftl/state`)

**v43 contract shape: confirmed compliant.**

Published retained, QoS 1, at `TelemetryPublishRateHz` (10 Hz).

### New payload shape

```json
{
  "armed": false,
  "phase": "idle",
  "progress": 0.0,
  "destination": null,
  "range_au": 0.0,
  "signal_lag_s": 0.0,
  "power_kw": 0,
  "power_max_kw": 500
}
```

### Cooldown phase: implemented

`FtlPhase.Complete` is **removed** and replaced by `FtlPhase.Cooldown`.

State machine: `Idle → Charging → Ready → Jumping → Cooldown → Idle`

- **Abort path (overheat):** `Charging/Jumping → Cooldown` (was `→ Idle` in batch 7).  
  Arm is force-cleared on abort, as before.
- **Deliberate disarm:** `Charging/Ready → Idle` (unchanged — no fault, no cooldown).
- **Cooldown guard:** Arm/VECTOR/Jump are no-ops during Cooldown.
  The `switch` on `_ftlPhase` has a `Cooldown` case that only counts the timer.
  Both action buttons are already disabled by `ControlPanelsWindow`'s
  `phase != Idle` guard, so UI reflects this correctly.

`FtlCooldownDuration` export, default `5f` seconds.

### Signal lag: implemented

`signal_lag_s` behaviour:

| Phase | Value |
|---|---|
| Idle | `0.0` |
| Charging | `(elapsed / duration) × MaxSignalLagS` (ramps 0 → max) |
| Ready | `MaxSignalLagS` (held) |
| Jumping | `MaxSignalLagS` (held — the "jump moment") |
| Cooldown | `(1 − elapsed/duration) × MaxSignalLagS` (decays max → 0) |

`MaxSignalLagS` export, default `4.0f`.

### Progress field

| Phase | Value |
|---|---|
| Idle | `0.0` |
| Charging | `elapsed / duration` (0 → 1) |
| Ready | `1.0` |
| Jumping | `0.0` |
| Cooldown | `1.0 − elapsed/duration` (1 → 0) |

### Field reality vs simplification

| Field | Reality |
|---|---|
| `armed` | Real |
| `phase` | Real (`"idle"`, `"charging"`, `"ready"`, `"jumping"`, `"cooldown"`) |
| `progress` | Real (see table above) |
| `destination` | Simplified: string name from `FtlState.Destinations[]` when armed, else `null`. 5 entries: `Sol`, `Alpha Centauri`, `Wolf 359`, `Tau Ceti`, `Proxima Centauri` |
| `range_au` | Simplified: fixed fiction per destination (`0.5 / 1.4 / 2.8 / 4.1 / 7.2 AU`). `0.0` when not armed |
| `signal_lag_s` | Real (see above) |
| `power_kw` | **Placeholder:** `0` at Idle, `340` otherwise |
| `power_max_kw` | **Hardcoded:** `500` |

### ControlPanelsWindow FTL UI changes

Adjusted for Cooldown replacing Complete:
- VECTOR LED: orange during Charging/Ready/Jumping (no change); off during Cooldown
- JUMP LED: blinking green during Jumping, **solid green during Cooldown** (completion signal)
- Status label: `"COOLDOWN"` during Cooldown phase
- VECTOR button: disabled when `phase != Idle` (includes Cooldown — no change needed)
- JUMP button: disabled when `phase != Ready` (no change needed)

---

## Alerts (`coldorbit/output/alerts`)

**New topic — implemented.**

Published retained, QoS 1. Full array on every change. `[]` when no alerts active.

Managed by `PlayerShip.UpdateAlerts()`, called every physics frame, publishes only on
transitions (raise or clear). Also republished by `SimBus.PublishCurrentAlerts()` on
every broker reconnect so the touchscreen recovers correct state after a broker restart.

### Alert IDs: stable

| Alert | Stable ID | Severity | System | Message |
|---|---|---|---|---|
| Propulsion overheat | `alert_engines_overheat` | `warning` | `engines` | `ENGINE OVERHEAT` |
| FTL charge aborted | `alert_ftl_aborted` | `caution` | `ftl` | `FTL CHARGE ABORTED` |

IDs are string literals. A second instance of the same alert (e.g. second overheat
event) reuses the same ID — correct, since only one of each can be active at a time.

### Raise/clear conditions

**Overheat:** raised on `false→true` transition of `_propulsionOverheated`; cleared on
`true→false`. Sourced from the existing `HandleThrust` temperature model.

**FTL aborted:** raised on `false→true` transition of `_ftlAborted`; cleared when
`_ftlAborted` is reset to `false` at the start of a new Charging attempt (i.e. when a
new VECTOR press succeeds). This matches "clear when a new VECTOR press clears it."

---

## Stub topics

All published from `SimBus.PublishStartupStubs()` on every broker connection.
All retained, QoS 1. **MOCK DATA — no real sim logic for any of these systems.**

| Topic(s) | Notes |
|---|---|
| `coldorbit/output/engineering/<system>/state` × 9 | `health: 100`, `disabled: false`, `repair_queue_position: null`, `effects: []`, `repair_eta_seconds: null`. Power-bearing systems (`weapons`, `engines`, `ftl`, `utility_1`–`4`): `power_allocated: 200`, `power_unit: "kW"`, `power_max: 500`. `reactor` and `hull`: power fields null |
| `coldorbit/output/comms/log` | 2-message array (1 incoming / 1 outgoing) |
| `coldorbit/output/comms/targets` | 1-contact array: `contact_001`, Harlan Voss, Independent, Light Freighter, 1240 m |
| `coldorbit/output/turrets/dorsal/state` | `armed: false`, `fire_mode: "lethal"`, `lock_state: "none"`, targets null, `ammo_loaded: "Kinetic Slug"`, 2 ammo types |
| `coldorbit/output/turrets/ventral/state` | Same shape |
| `coldorbit/output/missiles/fore_port/state` | `armed: false`, `status: "loaded"`, `missile_type: "Seeking"`, `lock_state: "none"`, targets null |
| `coldorbit/output/missiles/fore_starboard/state` | Same shape |
| `coldorbit/output/missiles/aft_port/state` | Same shape |
| `coldorbit/output/missiles/aft_starboard/state` | Same shape |
| `coldorbit/output/hardpoints/1/module` – `4/module` | `category: "empty"`, `name: null`, `armed: false` |

### `loadout-unlocked` — TEMPORARY

`coldorbit/output/ship/loadout-unlocked` publishes `"false"` (bare string, matching
the mock script's format) on startup. **Changed from the spec's suggested `true`**:
publishing `true` on every reconnect would clobber a real game-state trigger and
interfere with testing the locked state. Use `mock/set-loadout-unlocked.sh true`
during development to open the loadout screen manually.

**TODO:** replace stub with real game-state trigger when loadout unlock logic exists.

---

## SimBus restructuring

- `SimBus.AlertsState` — new class: `List<AlertEntry> Active`.
- `SimBus.AlertEntry` — new sealed record: `Id`, `Severity`, `System`, `Message`, `TimestampS`.
- `SimBus.Alerts` — new property, type `AlertsState`.
- `SimBus.PropulsionState.PublishTelemetry` — signature extended: `accelerationMs2`,
  `throttleInput`, `reverseEnabled` added.
- `SimBus.FtlState.PublishTelemetry` — signature extended: `progress`, `signalLagS` added.
  `chargeProgress` / `jumpProgress` kept for `ControlPanelsWindow` status label.
- `SimBus.FtlState.Destinations` — extended to 5 entries (added `"Proxima Centauri"`).
- `SimBus.FtlState.DestinationRangesAu` — new parallel AU array.
- `FtlPhase.Complete` — **removed**; replaced by `FtlPhase.Cooldown`.

---

## Judgment calls and deviations

1. **`loadout-unlocked` publishes `"false"`** — see above.

2. **Propulsion state publish rate** — rate-limited at 10 Hz (not every-change) because
   continuous fields make every-change equivalent to every-frame.

3. **`power_kw` in FTL** — `0` at Idle, `340` otherwise (not a flat `340` everywhere).

4. **`acceleration_ms2` can go negative** — velocity delta / dt is signed. Correct
   physics; will be noisy at low speeds due to Godot's frame jitter.

5. **All three engine `temp_c` values are identical** — one thermal model; spec permits this.

6. **CS8632 nullable warnings** — 11 warnings from `?` annotations without `#nullable
   enable`. Correct at runtime. Fix by adding `<Nullable>enable</Nullable>` to
   `sim-core.csproj` when/if the project adopts nullable reference types.

---

## Not done this batch (per spec)

- Subscribing to touchscreen input topics (loadout confirm, turret ammo/missile type select)
- Engineering repair queue logic
- Real turret/missile/comms/hardpoint game logic
- Godot `ControlPanelsWindow` UI changes beyond FTL Cooldown LED/label fixes
- Hardpoint telemetry topics (`/telemetry`, not `/module`)
- Gravity/SOI model (`soi_body` remains `"Unknown"`)

---

## Files changed

- [`scripts/SimBus.cs`](scripts/SimBus.cs) — `AlertsState`/`AlertEntry` types; `Alerts`
  property; `PropulsionState`/`FtlState` signature extensions; 5th destination + AU array;
  `FtlPhase.Cooldown` (replaces `Complete`); `PublishStartupStubs()`, `PublishCurrentAlerts()`,
  `PublishEngineeringStubs()`, `PublishCommsStubs()`, `PublishTurretStubs()`,
  `PublishMissileStubs()`, `PublishHardpointStubs()` in `OnMqttConnected`.
- [`scripts/PlayerShip.cs`](scripts/PlayerShip.cs) — `PowerPerEnginekW`, `FtlCooldownDuration`,
  `MaxSignalLagS` exports; `_thrustInput`, `_reverseEnabled`, `_previousVelocity`,
  `_ftlSignalLagS`, `_missionTimeS`, alert tracking fields; Cooldown phase in `HandleFtl`;
  signal lag + progress calculation; `PublishTelemetry(dt)` extended; `PublishMqttState()`
  now alerts-only; `PublishMqttTelemetry(dt)` now publishes propulsion + FTL state;
  `PublishPropulsionState()` / `PublishFtlState()` rewritten for v43 payload;
  `UpdateAlerts()` / `PublishAlertsIfChanged` alert management.
- [`scripts/ControlPanelsWindow.cs`](scripts/ControlPanelsWindow.cs) — FTL LED/status
  label updated for `FtlPhase.Cooldown`; `FtlPhase.Complete` references removed.

## Versions tested against

- Godot 4.7.1 (.NET / C#)
- .NET 8.0 (`net8.0` target framework)
- `Godot.NET.Sdk/4.7.1`
- MQTTnet 5.2.0.1603
- Mosquitto 2.1.2 (local broker)
- Build: `dotnet build sim-core.csproj` → **0 errors**

Runtime MQTT round-trip was verified in batch 6/7. No changes to transport layer this batch.
Touchscreen rendering cannot be confirmed headless — start the broker, run `mock/mock-everything.sh`
to compare baseline mock vs. live sim-core output.

---

Copy this file's contents into the Cold Orbit project conversation — the master plan
doc lives in that conversation's Knowledge Base, not this repo.
