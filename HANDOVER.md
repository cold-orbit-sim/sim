# Handover: Cold Orbit — sim-core, batch 16 (FTL destination select)

## Context
Cold Orbit sim-core — Godot 4.7 / C# / .NET 8. The FTL jump-drive state
machine (batches 5/8) uses a simple placeholder destination list. The
touchscreen Map view (aux-display-client, v66) is now built and subscribes
to `coldorbit/output/ftl/target` and `coldorbit/output/ftl/system`. This
batch replaces the placeholder list with the real 26-system / 80-planet
Drift data and implements the two-layer Dest select navigation described
in §7.6, publishing the correct MQTT contracts so the Map view works
end-to-end.

**Read §3.1b (ftl/target and ftl/system contracts) and §7.6 (FTL panel
Dest select) in full before starting.** The complete system and planet
data is below — embed it directly in code as a static data structure,
don't hardcode it ad-hoc.

---

## The Drift data (embed this verbatim as a static data structure)

```csharp
// DriftData.cs — static, no MonoBehaviour, no scene dependency
public static class DriftData
{
    public record Planet(string Name);

    public record StarSystem(
        string Id,          // single letter A–Z
        string StarName,
        string StarType,
        Planet[] Planets);  // empty array for Xelgrave

    public static readonly StarSystem[] Systems = new[]
    {
        new StarSystem("A", "Aurivane",  "B-type",             new[] { new Planet("Ashra"), new Planet("Avonis"), new Planet("Ardal") }),
        new StarSystem("B", "Belkarra",  "Binary",             new[] { new Planet("Bexar"), new Planet("Boreth"), new Planet("Brynhal"), new Planet("Belsara") }),
        new StarSystem("C", "Cathrax",   "M-type",             new[] { new Planet("Caldris"), new Planet("Corvax") }),
        new StarSystem("D", "Duskane",   "Red giant",          new[] { new Planet("Dorral") }),
        new StarSystem("E", "Eshalon",   "G-type",             new[] { new Planet("Esben"), new Planet("Eirlys"), new Planet("Endra"), new Planet("Elmara"), new Planet("Ethran"), new Planet("Evanth") }),
        new StarSystem("F", "Favrenn",   "F-type",             new[] { new Planet("Faelric"), new Planet("Ferrun"), new Planet("Farsa"), new Planet("Fenvale") }),
        new StarSystem("G", "Gethryn",   "White dwarf",        new[] { new Planet("Grael"), new Planet("Gorvane"), new Planet("Ghesta"), new Planet("Grendal"), new Planet("Golstrav") }),
        new StarSystem("H", "Hessarin",  "K-type",             new[] { new Planet("Hessik"), new Planet("Halvorn"), new Planet("Hendra"), new Planet("Hurath"), new Planet("Hovash") }),
        new StarSystem("I", "Ivrenna",   "M-type",             new[] { new Planet("Isvard"), new Planet("Ithran"), new Planet("Ilmara"), new Planet("Ivorn") }),
        new StarSystem("J", "Jovendra",  "A-type",             new[] { new Planet("Jareth"), new Planet("Jendra"), new Planet("Joras") }),
        new StarSystem("K", "Kerath",    "K-type",             new[] { new Planet("Kael") }),
        new StarSystem("L", "Loreth",    "Brown dwarf",        new[] { new Planet("Loran") }),
        new StarSystem("M", "Mireth",    "K-type",             new[] { new Planet("Maldrin"), new Planet("Myrrhen"), new Planet("Movane"), new Planet("Marresh") }),
        new StarSystem("N", "Nyxaros",   "Pulsar",             new[] { new Planet("Nyxa"), new Planet("Noross") }),
        new StarSystem("O", "Osmerin",   "F-type",             new[] { new Planet("Osric"), new Planet("Orvane"), new Planet("Othel"), new Planet("Ostrava") }),
        new StarSystem("P", "Perlan",    "M-type",             new[] { new Planet("Perrek"), new Planet("Pyrhen"), new Planet("Pelvos") }),
        new StarSystem("Q", "Quorven",   "K-type",             new[] { new Planet("Quel"), new Planet("Quoraith"), new Planet("Quenna") }),
        new StarSystem("R", "Rovash",    "M-type",             new[] { new Planet("Rethis"), new Planet("Rovane") }),
        new StarSystem("S", "Savarin",   "G-type",             new[] { new Planet("Sevrin"), new Planet("Sorvane"), new Planet("Sethral"), new Planet("Shaldris"), new Planet("Sarnoth") }),
        new StarSystem("T", "Threnval",  "A-type",             new[] { new Planet("Tessin"), new Planet("Thara") }),
        new StarSystem("U", "Undrasi",   "M-type",             new[] { new Planet("Ulvane"), new Planet("Ushira"), new Planet("Undrel"), new Planet("Uvaris") }),
        new StarSystem("V", "Vantheris", "A-type",             new[] { new Planet("Vessik"), new Planet("Varrow"), new Planet("Vondrel"), new Planet("Vashera"), new Planet("Vireth") }),
        new StarSystem("W", "Wyvane",    "B-type",             new[] { new Planet("Wrenna"), new Planet("Wyndel") }),
        new StarSystem("X", "Xelgrave",  "Black hole remnant", System.Array.Empty<Planet>()),
        new StarSystem("Y", "Yrendal",   "M-type",             new[] { new Planet("Yrengar"), new Planet("Yolvane"), new Planet("Ysendra") }),
        new StarSystem("Z", "Zerath",    "K-type",             new[] { new Planet("Zaelin") }),
    };

    // Index lookup: 'A'=0, 'B'=1, ... 'Z'=25
    public static int SystemIndex(string id) => id[0] - 'A';
    public static StarSystem System(string id) => Systems[SystemIndex(id)];
}
```

**Current system (where the ship is):** hardcode as `"K"` (Kerath) for
now — no real travel system exists yet. Expose as
`[Export] public string CurrentSystemId = "K"` on `PlayerShip` or
`SimBus.FtlState` so it can be changed without a rebuild when real travel
is implemented.

---

## Task 1 — Destination selection state in SimBus.FtlState

Replace the existing `DestinationIndex` int and `Destinations[]` string
array with a two-layer selection model:

```csharp
// In SimBus.FtlState:
public string SelectedSystemId;      // A–Z, default "K" (current system)
public int SelectedPlanetIndex;      // -1 = star selected, ≥0 = planet index within system
```

Helper properties (read-only, derived):
```csharp
public bool IsStarSelected => SelectedPlanetIndex < 0;
public DriftData.StarSystem SelectedSystem
    => DriftData.System(SelectedSystemId);
public bool IsInCurrentSystem(string currentSystemId)
    => SelectedSystemId == currentSystemId;
```

Initialise: `SelectedSystemId = "K"`, `SelectedPlanetIndex = -1`
(Kerath star selected by default — the ship is already there, so this
is the natural idle state).

---

## Task 2 — Navigation logic (Prev/Next input)

The Dest prev/next buttons are already wired to publish
`coldorbit/input/ftl/command` with `destination_index`. Replace that
with explicit prev/next actions. The input topic is already
`coldorbit/input/ftl/command` — extend the payload:

```json
{ "dest_action": "next", "updated_at": 1754561234567 }
{ "dest_action": "prev", "updated_at": 1754561234567 }
```

In `SimBus.OnMqttMessageReceived`, when `dest_action` is present:

**Next logic:**

if currently on a star (SelectedPlanetIndex == -1):
if SelectedSystemId == CurrentSystemId AND system has planets:
// Drill into this system — select first planet
SelectedPlanetIndex = 0
else:
// Advance to next star letter (wrap A→Z→A, skip nothing)
SelectedSystemId = next letter (wrapping)
SelectedPlanetIndex = -1
else (currently on a planet):
if SelectedPlanetIndex < system.Planets.Length - 1:
// Next planet in system
SelectedPlanetIndex++
else:
// Was on last planet — back out to star level, advance star
SelectedSystemId = next letter (wrapping)
SelectedPlanetIndex = -1


**Prev logic (mirror image):**

if currently on a star:
// Go to previous star letter (wrap Z→A)
SelectedSystemId = prev letter (wrapping)
// Select last planet of previous system if it has planets AND
// prev system == CurrentSystemId? No — prev always lands on star
SelectedPlanetIndex = -1
else (currently on a planet):
if SelectedPlanetIndex > 0:
SelectedPlanetIndex--
else:
// Was on first planet — back out to the star (same system)
SelectedPlanetIndex = -1


**Key behaviour decisions to confirm:**
- Xelgrave (X) has no planets — Next on Xelgrave's star just advances
  to Y as normal. No special handling needed.
- The current system (K) is the only one where drilling into planets is
  possible via the Next action. All other systems only show star targets.
  This matches §2's two-jump mechanic: long-range jumps target a star,
  in-system jumps target a planet — and you can only pick planets in the
  system you're already in (i.e., for the short in-system jump).
- Letter wrapping: Z+1 = A, A-1 = Z. Simple modular arithmetic on index.

**Update `ControlPanelsWindow` FTL tab** to publish `dest_action: "next"`
/ `"prev"` from the existing Dest ◀ ▶ buttons instead of the old
`destination_index` increment/decrement. The buttons already exist —
just change what they publish.

---

## Task 3 — Distance and spool time calculation

No real star map coordinates exist in sim-core. Use a simple placeholder
distance model based on alphabetical distance from K:

```csharp
private static float PlaceholderDistanceAu(string fromSystemId, string toSystemId)
{
    int from = fromSystemId[0] - 'A';
    int to   = toSystemId[0]   - 'A';
    // Shortest wrap-around distance on the 26-letter ring
    int diff = Math.Abs(to - from);
    int dist = Math.Min(diff, 26 - diff);
    // Scale: adjacent system = 1.5 AU, opposite side of ring = ~19.5 AU
    return 1.5f + dist * 1.4f;
}
```

For planets within the current system, add a small flat increment
(suggest `0.1f × (planetIndex + 1)` AU) to the star's distance — the
in-system jump is short, so the difference between the star and its
planets should feel small.

```csharp
private static int SpoolTimeSeconds(float distanceAu)
{
    // Reuse the existing FTL charge formula: BaseChargeTime + Distance * ChargeTimePerDistanceUnit
    // where these are the exports already on PlayerShip (or SimBus.Ftl)
    return (int)(BaseChargeTime + distanceAu * ChargeTimePerDistanceUnit);
}
```

Read `BaseChargeTime` and `ChargeTimePerDistanceUnit` from
`SimBus.Ftl` exports — they're already there from batch 5. Don't
hardcode them.

---

## Task 4 — Publish ftl/target and ftl/system

Call `PublishFtlNavTarget()` whenever the selection changes (in the
dest_action handler, and on startup/reconnect). Both topics retained,
QoS 1.

**`coldorbit/output/ftl/target`:**

```csharp
private void PublishFtlNavTarget()
{
    var sys = DriftData.System(Ftl.SelectedSystemId);
    float dist = PlaceholderDistanceAu(CurrentSystemId, Ftl.SelectedSystemId);

    object payload;

    if (Ftl.IsStarSelected)
    {
        if (Ftl.SelectedSystemId == CurrentSystemId && sys.Planets.Length == 0)
        {
            // Edge case: Xelgrave or similar — no planets, star-only target
        }
        payload = new {
            type = "star",
            system_id = sys.Id,
            name = sys.StarName,
            star_type = sys.StarType,
            planet_count = sys.Planets.Length,
            distance_au = MathF.Round(dist, 1),
            spool_time_s = SpoolTimeSeconds(dist)
        };
    }
    else
    {
        var planet = sys.Planets[Ftl.SelectedPlanetIndex];
        float planetDist = dist + 0.1f * (Ftl.SelectedPlanetIndex + 1);
        payload = new {
            type = "planet",
            system_id = sys.Id,
            name = planet.Name,
            system_name = sys.StarName,
            star_type = sys.StarType,
            distance_au = MathF.Round(planetDist, 1),
            spool_time_s = SpoolTimeSeconds(planetDist)
        };
    }

    Mqtt.Publish("coldorbit/output/ftl/target", JsonSerializer.Serialize(payload),
                 retain: true, qos: MqttQualityOfServiceLevel.AtLeastOnce);
}
```

On startup with no selection, publish `{ "type": "none" }` — then
immediately publish the default (Kerath star) on the next tick.

**`coldorbit/output/ftl/system`:**
- When a planet is selected: publish the full system payload with
  `system_id`, `star_name`, `star_type`, and `planets[]`.
- When a star or no target: publish `{ "system_id": null }`.

```csharp
private void PublishFtlSystem()
{
    if (!Ftl.IsStarSelected)
    {
        var sys = DriftData.System(Ftl.SelectedSystemId);
        var payload = new {
            system_id = sys.Id,
            star_name = sys.StarName,
            star_type = sys.StarType,
            planets = sys.Planets.Select(p => new { name = p.Name }).ToArray()
        };
        Mqtt.Publish("coldorbit/output/ftl/system",
                     JsonSerializer.Serialize(payload),
                     retain: true, qos: MqttQualityOfServiceLevel.AtLeastOnce);
    }
    else
    {
        Mqtt.Publish("coldorbit/output/ftl/system",
                     JsonSerializer.Serialize(new { system_id = (string?)null }),
                     retain: true, qos: MqttQualityOfServiceLevel.AtLeastOnce);
    }
}
```

---

## Task 5 — Update the FTL state topic

`coldorbit/output/ftl/state` currently publishes `destination` as a
string name and `range_au` as a fixed float. Update these to use the
real selected values:

- `destination`: if star selected → star name; if planet selected →
  planet name; if none → `null`
- `range_au`: use `PlaceholderDistanceAu` output (or planet dist)

The other fields (`phase`, `progress`, `signal_lag_s`, `power_kw`,
`power_max_kw`) are unchanged.

---

## Task 6 — Update ControlPanelsWindow FTL tab display

The existing FTL tab in `ControlPanelsWindow` has a dest label showing
the old `destination_index` value. Update it to show the real destination
name: `SyncFtlFromBus()` should set the label to whatever
`Ftl.SelectedSystem.StarName` + (if planet selected) ` / planetName`
gives. This is the in-Godot dev panel — it doesn't need to match the
touchscreen's Map view layout, just be readable.

---

## Task 7 — Update admin panel FTL tab

The admin panel FTL tab currently has an Armed toggle, a Phase dropdown,
and a destination field. Replace the destination field with:
- System dropdown (A–Z, showing letter + star name)
- Planet index spinner (−1 for star, 0..n for planet) — or a dropdown
  showing star / planet names — your choice, note it in the handback
- Both write to `SimBus.Ftl.SelectedSystemId` / `SelectedPlanetIndex`
  and call `PublishFtlNavTarget()` + `PublishFtlSystem()`

---

## Guardrails
- Embed the Drift data verbatim from the tables above — don't invent,
  abbreviate, or summarise. 26 systems, 80 planets, exact names as given.
- Don't implement actual jump travel between systems — the state machine
  still teleports the ship along heading as a placeholder. This batch is
  destination *selection*, not destination *arrival*.
- Don't touch the existing FTL phase state machine (Idle/Charging/Ready/
  Jumping/Cooldown), just the destination select layer on top of it.
- `ftl/target` and `ftl/system` must both publish on every dest change,
  on startup, and on broker reconnect.
- Don't touch flight model, propulsion, hardpoints, alerts, or any other
  system.

## When you're done: hand back
Write `HANDOVER-BACK.md` covering:
- Confirmation all 26 systems and 80 planets are in `DriftData` (check
  the counts, don't just assert it)
- Confirmation Next/Prev logic works correctly at the boundaries:
  Z→A wrap, A→Z wrap, current-system drill-in to planets, back-out
  from first planet to star
- What the admin panel destination control looks like
- Example `mosquitto_sub` output for `ftl/target` and `ftl/system` for
  at least: a star (non-current system), a planet (current system), and
  the "no selection" state
- Confirmation `ftl/state` destination field is now the real name
- Godot 4.7 / .NET 8 confirmed

Then tell the user to copy that back into the Cold Orbit project
conversation on claude.ai.
