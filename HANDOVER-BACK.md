# Handover Back — Batch 13: Remaining Hardpoint Modules

**Godot 4.7 / .NET 8 — build: 0 errors, 35 warnings**
(35 CS8632 nullable warnings — 33 pre-existing, 2 new from new nullable fields in HardpointSlot; no new warning categories)

---

## Summary of what was implemented

### Task 1 — `SimBus.HardpointSlot` extension

All fields added with correct defaults:

```csharp
// --- Cargo/Storage ---
public float FillPct     { get; set; }           // default 0
public string? Contents  { get; set; }           // default null
public float? TempC      { get; set; }           // default null (reefer only)
public float? TempMin    { get; set; }           // default null
public float? TempMax    { get; set; }           // default null

// --- Sensor/EW ---
public bool ScannerModeActive { get; set; }      // default false
public bool ScannerModeBeam   { get; set; }      // default false
public float ScannerBearing   { get; set; }      // default 0; 0–360 wrapping
public bool StealthOn         { get; set; }      // default false

// --- Defense ---
public bool ShieldOn                  { get; set; }      // default false
public string ShieldSelectedFacing    { get; set; } = "fore";
public Dictionary<string, float> ShieldStrengths { get; set; }
    // = { "fore":0.5, "aft":0.5, "port":0.5, "starboard":0.5 }
public bool PdEngaged          { get; set; }     // default false
public bool MissileLockWarning { get; set; }     // default false
public int  DecoyCount         { get; set; } = 12;
```

All fields reset to defaults in `HandleLoadoutConfirm` per the same pattern as base fields.

---

### Task 2 — `encoder_b` subscription

`coldorbit/input/hardpoints/+/encoder_b` subscribed in `_Ready()`. Payload shape same as `encoder_a`.

Wired per contract:
- **Long-range Scanner Array:** adjusts `ScannerBearing` (±1°/detent, 0–360 wrapping) **only when** `ScannerModeActive && ScannerModeBeam`; logs and ignores otherwise. Calls `PublishHardpointModule` on change to keep `updated_at` fresh (bearing is not in the module state payload per the contract table).
- **Prospecting Suite:** adjusts ore filter index (same `StepProspectingIndex` helper as encoder_a).
- **Stealth/ECM Package:** adjusts `Intensity` (power draw) ±0.05/detent, 0–1 clamp.
- **All others:** log and ignore.

---

### Task 3 — Soft-key handling (all categories)

`HandleSoftkey` now dispatches by category to dedicated helpers:

**`HandleSoftkeyUtilityTool`** — unchanged behaviour from batch 12.

**`HandleSoftkeyUtilityTool` (cargo_storage)** — all SKs: no-op, no log.

**`HandleSoftkeySensorEW`:**
- Scanner Array: SK5 → toggle `ScannerModeActive`; SK6 → toggle `ScannerModeBeam` (guarded: logs + no-op if not Active)
- Prospecting Suite: SK5 → "SCAN triggered (no gameplay outcome)" logged; SK6 → decrement index; SK7 → increment index
- Stealth/ECM: SK5 → toggle `StealthOn`

**`HandleSoftkeyDefense`:**
- Shield Generator: SK1–SK4 → select facing (fore/aft/port/starboard); SK5 → toggle `ShieldOn`
- Point-Defense Turret: SK5 → toggle `PdEngaged`
- Decoy/Flare: SK5 → `DecoyCount--` (floor 0)

**Encoder A extensions** (beyond utility, which is unchanged):
- Scanner Array: ±`Intensity` 0.05/detent, 0–1 clamp (range)
- Prospecting Suite: ±ore filter index via `StepProspectingIndex`
- Stealth/ECM: ±`Intensity` 0.05/detent (frequency)
- Shield Generator: ±`ShieldStrengths[ShieldSelectedFacing]` 0.05/detent → **calls `PublishHardpointModule`** (shield_strengths is in module state)
- All others: no-op

---

### Task 4 — `PublishHardpointModule` (per-module payload)

Replaced inline serialization with `BuildModulePayload(HardpointSlot hp, int slot)` returning `Dictionary<string, object?>`. Each module includes only the fields listed in the contract table:

| Module | Extra fields |
|---|---|
| Mining Laser | none |
| Cutting/Welding Torch | `mode` |
| Grapple/Winch Rig | `attached` |
| Standard Pod | `fill_pct`, `contents` |
| Reefer Pod | `fill_pct`, `contents`, `temp_c`, `temp_min`, `temp_max` |
| Ore Hopper | `fill_pct`, `contents` |
| Long-range Scanner Array | `scanner_mode_active`, `scanner_mode_beam` |
| Prospecting Suite | none |
| Stealth/ECM Package | `stealth_on` |
| Deflector Shield Generator | `shield_on`, `shield_selected_facing`, `shield_strengths` |
| Point-Defense Turret Pod | `pd_engaged` |
| Decoy/Flare Dispenser | `missile_lock_warning`, `decoy_count` |
| empty | none |

Example payloads from `mosquitto_sub -t 'coldorbit/output/hardpoints/+/module' -v`:

**cargo_storage / Reefer Pod (slot 2):**
```json
{
  "slot": 2, "category": "cargo_storage", "name": "Reefer Pod",
  "armed": false, "updated_at": "2026-08-07T12:00:00Z",
  "fill_pct": 0, "contents": null, "temp_c": null, "temp_min": null, "temp_max": null
}
```

**sensor_ew / Long-range Scanner Array (slot 1):**
```json
{
  "slot": 1, "category": "sensor_ew", "name": "Long-range Scanner Array",
  "armed": true, "updated_at": "2026-08-07T12:00:00Z",
  "scanner_mode_active": true, "scanner_mode_beam": false
}
```

**defense / Deflector Shield Generator (slot 3):**
```json
{
  "slot": 3, "category": "defense", "name": "Deflector Shield Generator",
  "armed": true, "updated_at": "2026-08-07T12:00:00Z",
  "shield_on": true, "shield_selected_facing": "fore",
  "shield_strengths": {"fore": 0.7, "aft": 0.5, "port": 0.5, "starboard": 0.5}
}
```

---

### Task 5 — `PublishHardpointTelemetry` (all modules, correct units)

**Utility tool telemetry corrected to real units** (batch 12 used % — this is the spec update):
- Mining Laser / Cutting/Welding Torch: `INTNS`, `Intensity×500`, `kW`, 0–500
- Grapple/Winch Rig: `LEN`, `Intensity×200`, `m`, 0–200

New modules:
- Long-range Scanner Array: `RANGE`, `Intensity×500`, `km`, 0–500
- Prospecting Suite: `IDX`, `round(Intensity×4)`, `""`, 0–4
- Stealth/ECM Package: `FREQ`, `Intensity×100`, `MHz`, 0–100
- Deflector Shield Generator: `STR`, `ShieldStrengths[ShieldSelectedFacing]×100`, `%`, 0–100

Omitted (no publish): Standard Pod, Reefer Pod, Ore Hopper, Point-Defense Turret Pod, Decoy/Flare Dispenser, empty.

Example from `mosquitto_sub -t 'coldorbit/output/hardpoints/+/telemetry' -v`:
```json
{"slot":1,"label":"INTNS","value":125.0,"unit":"kW","min":0,"max":500,"active":false,"mode":null}
{"slot":3,"label":"STR","value":70.0,"unit":"%","min":0,"max":100,"active":false,"mode":null}
```

---

### Task 6 — Admin panel updates

`KnownModules` updated to correct contract names:
- cargo: Standard Pod / Reefer Pod / Ore Hopper
- sensor_ew: Long-range Scanner Array / Prospecting Suite / Stealth/ECM Package
- defense: Deflector Shield Generator / Point-Defense Turret Pod / Decoy/Flare Dispenser

**Category-specific control groups** added per slot (show/hide via `UpdateHardpointControlVisibility`):
- `UtilityGroup`: Mode dropdown, Attached check — visible only for `utility_tool`
- `CargoGroup`: Fill% slider, Contents field; sub-group `ReeferGroup` (TempC/TempMin/TempMax) visible only for Reefer Pod
- `SensorGroup`: Scanner Active/Beam checks, Stealth On check — visible only for `sensor_ew`
- `DefenseGroup`: Shield On, Facing dropdown, 4× strength sliders, PD Engaged, Missile Lock Warning, Decoy Count — visible only for `defense`

All controls write directly to `SimBus.Instance.Hardpoints[slot-1]` then call `AdminUpdateHardpoint` which publishes. Live-mirrored via `SyncHardpointsFromBus` every frame with NoSignal/`_mirrorActive` guards.

---

## How to verify

**encoder_b (scanner bearing):**
```bash
mosquitto_pub -t coldorbit/input/hardpoints/1/arm -m '{"state":1,"updated_at":1000}'
# First activate scanner mode (SK5 = Active, SK6 = Beam)
mosquitto_pub -t coldorbit/input/hardpoints/1/softkey -m '{"key":"SK5","state":1,"updated_at":1001}'
mosquitto_pub -t coldorbit/input/hardpoints/1/softkey -m '{"key":"SK6","state":1,"updated_at":1002}'
# Now encoder_b should move bearing
mosquitto_pub -t coldorbit/input/hardpoints/1/encoder_b -m '{"delta":10,"updated_at":1003}'
# updated_at in module payload should change; no bearing field in payload (internal only)
```

**encoder_b (scanner Array, not in Active+Beam):**
```bash
mosquitto_pub -t coldorbit/input/hardpoints/1/encoder_b -m '{"delta":5,"updated_at":2000}'
# GD.Print: "encoder_b slot 1 (Long-range Scanner Array) — ignored, not Active+Beam"
```

**Shield strength via encoder_a:**
```bash
mosquitto_pub -t coldorbit/input/hardpoints/3/arm -m '{"state":1,"updated_at":1000}'
mosquitto_pub -t coldorbit/input/hardpoints/3/encoder_a -m '{"delta":3,"updated_at":1001}'
# Module payload should show shield_strengths.fore updated by +0.15
```

**Decoy launch:**
```bash
mosquitto_pub -t coldorbit/input/hardpoints/4/arm -m '{"state":1,"updated_at":1000}'
mosquitto_pub -t coldorbit/input/hardpoints/4/softkey -m '{"key":"SK5","state":1,"updated_at":1001}'
# Module payload: "decoy_count": 11
```

**Telemetry units (utility tools):**
```bash
mosquitto_sub -t 'coldorbit/output/hardpoints/+/telemetry' -v
# Expect "unit":"kW" max 500 for Mining Laser/Torch, "unit":"m" max 200 for Grapple
```

---

## SKs that resulted in "log and ignore"

| Module | SK | Reason |
|---|---|---|
| Prospecting Suite | SK5 (SCAN) | No gameplay outcome yet — logged to GD.Print |
| Long-range Scanner Array | SK6 when not Active | Guard: Beam mode only meaningful in Active mode |
| Scanner Array encoder_b | encoder_b when not Active+Beam | Guard per contract |
| cargo_storage any SK | All | No cargo manipulation inputs yet |
| All sensor_ew modules | SKs not in contract | Log and ignore default case |
| All defense modules | SKs not in contract | Log and ignore default case |

---

## Deviations and judgment calls

- **`ScannerBearing` not in module state payload**: The contract table for Long-range Scanner Array lists only `scanner_mode_active` and `scanner_mode_beam`. Bearing is stored in `HardpointSlot.ScannerBearing` for potential future use but omitted from the MQTT payload. `PublishHardpointModule` is still called on bearing change (keeps `updated_at` fresh per handover note).
- **encoder_a for Shield Generator calls `PublishHardpointModule`**: `shield_strengths` is in the module state, so changes to it (encoder_a on Shield Generator) trigger an immediate publish rather than waiting for the next telemetry tick. Other encoder_a changes (intensity-only) do not publish module state.
- **Prospecting Suite `Intensity` storage**: Stored as normalized 0–1 (consistent with other modules). Integer index 0–4 is `round(Intensity × 4)`. Encoder and softkey both work through `StepProspectingIndex` which converts round-trip. This means the admin Intensity slider (0–1) maps correctly to indices 0–4.
- **MissileLockWarning stays `false`**: Not driven by any gameplay mechanic yet. Admin panel exposes a checkbox for test injection, noted as test-only in the label.
- **cargo_storage SKs**: Silent no-op (no log needed per handover).

## TODOs for future batches
- Wire `ScannerBearing` into telemetry or module state once the display panel has a bearing readout
- Implement SCAN gameplay for Prospecting Suite SK5 (currently logged/ignored)
- Implement `MissileLockWarning` from gameplay (missile tracking system)
- Confirm loadout payload shape with display client (assumed `{"slots":{"1":{...}}}`)
