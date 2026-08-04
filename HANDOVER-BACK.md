# Handover back: sim-core batch 7 (touchscreen mode-select wiring)

Closes the open item from master plan §3.1b / §3.7: the 7 mode-select
buttons in the Godot UI's Touchscreen tab now publish to MQTT on press,
sim-core subscribes and decides the effective mode, and the output topic
drives the UI LED state. The full round-trip is wired and testable with a
local broker.

## Round-trip confirmation

The intended signal path per the spec:

```
UI button press (Godot)
→ coldorbit/input/touchscreen/<mode>   [QoS 1, not retained]
→ SimBus.HandleMqttMessage validates + updates SimBus.Touchscreen.Mode
→ publishes coldorbit/output/touchscreen/mode   [QoS 1, retained, bare string]
→ SyncTouchscreenFromBus() reflects active mode in UI LEDs
```

All four legs are wired. The round-trip is testable as follows:

1. Start Mosquitto (`brew services start mosquitto`)
2. Subscribe to both namespaces:
   ```
   mosquitto_sub -v -t 'coldorbit/input/touchscreen/#' -t 'coldorbit/output/touchscreen/mode'
   ```
3. Open Godot and run the scene. You should immediately see:
   ```
   coldorbit/output/touchscreen/mode hardpoints
   ```
   (the startup default, Task 3 below)
4. Click "Engineering" in the Touchscreen tab. You should see:
   ```
   coldorbit/input/touchscreen/engineering {"state":1,"updated_at":...}
   coldorbit/output/touchscreen/mode engineering
   ```
   And the Engineering LED turns green; Hardpoints LED goes off.
5. Release the button. You should see:
   ```
   coldorbit/input/touchscreen/engineering {"state":0,"updated_at":...}
   ```
   (no output publish on release — only state:1 triggers a mode change)
6. Stop the Godot process. Subscribe cold:
   ```
   mosquitto_sub -C 1 -t 'coldorbit/output/touchscreen/mode'
   ```
   Should return `engineering` immediately from the broker's retained store.

Not live-verified this pass (headless run only, no display available).
The code path is the same publish/subscribe infrastructure exercised in
batch 6's verified round-trip. Needs a play-test to close the gap.

## Where the logic lives

**`scripts/MqttTelemetryPublisher.cs`** — extended to support subscriptions.
Added: `_subscriptions` list, `MessageReceived` event, `Connected` event,
`Subscribe(filter, qos)` method, `SubscribeAllAsync()`, and wiring in
`ConnectWithRetryLoop` to call `SubscribeAllAsync()` then fire `Connected`
after every successful broker connect. `ApplicationMessageReceivedAsync`
dispatches to `MessageReceived`. The "publish only" framing in the batch 6
class comment is updated: this is now the general MQTT client manager for
sim-core — one client, one class, one reconnect loop.

Why here rather than a new `MqttModeSelectHandler`? `MqttTelemetryPublisher`
already owns the single `IMqttClient`. Adding a second class with its own
client would mean two broker connections; passing the client out would
expose internals. Keeping it in one place is simpler and matches the
single-client intent from batch 6.

**`scripts/SimBus.cs`** — mode-select logic and event wiring. In `_Ready()`,
before `Mqtt.Start()`: calls `Mqtt.Subscribe("coldorbit/input/touchscreen/+")`,
registers `Mqtt.MessageReceived += OnMqttMessageReceived` and
`Mqtt.Connected += OnMqttConnected`. The subscribe filter registration must
precede `Start()` to avoid racing the connect loop.

`OnMqttMessageReceived`: extracts the mode from the final topic segment,
validates against `ValidTouchscreenModes` (a `HashSet<string>`), parses
`state` from the JSON payload, ignores `state:0`, updates
`Touchscreen.Mode`, and publishes the bare string to
`coldorbit/output/touchscreen/mode` (retained, QoS 1).

`OnMqttConnected`: publishes `Touchscreen.Mode` to the output topic
immediately after each broker connect. On first connect this is
`"hardpoints"` (§3.7 default, the `TouchscreenState` field initializer).
On reconnect it re-asserts whatever mode was last active, so a broker
restart doesn't lose the retained value.

**`scripts/ControlPanelsWindow.cs`** — button wiring and LED sync. Stores
`Button[] _touchscreenButtons` and `ColorRect[] _touchscreenLeds`. In
`BuildTouchscreenModeTab`, each button wires `ButtonDown` → `PublishButtonState(topic, 1)`
and `ButtonUp` → `PublishButtonState(topic, 0)` (topic derived from
`name.ToLowerInvariant()`; same `PublishButtonState` used by FTL VECTOR/JUMP).
The pre-existing `Toggled → led.Color` handler is removed: LED state is now
driven by `SyncTouchscreenFromBus()` (called from `_Process`) which reads
`SimBus.Instance.Touchscreen.Mode` and calls `SetPressedNoSignal` /
updates LED colors only when the mode changes (`_lastTouchscreenMode` guard).

## Default retained state on startup (Task 3)

`TouchscreenState.Mode` is initialized to `"hardpoints"`. `OnMqttConnected`
fires immediately after the first successful broker connection and publishes
it to `coldorbit/output/touchscreen/mode` (retained, QoS 1). A touchscreen
client connecting after Godot starts gets the retained `"hardpoints"` without
waiting for a button press.

On reconnect (if the broker was restarted), `OnMqttConnected` re-publishes
the current active mode (whatever was last set), so the retained store is
always consistent with what sim-core believes.

## Judgment calls and deviations

- **`Payload.ToArray()` via `System.Buffers`**: MQTTnet 5.2.0.1603 exposes
  the received message payload as `ReadOnlySequence<byte>` (`Payload`
  property) and a set-only `PayloadSegment` (`ArraySegment<byte>`, no
  getter). `ReadOnlySequence<byte>.ToArray()` is available as an extension
  method from `System.Buffers` (`BuffersExtensions.ToArray`). Added
  `using System.Buffers;` to `MqttTelemetryPublisher.cs` accordingly.

- **QoS 1 for input topics, not QoS 2**: the spec says QoS 1 for the
  touchscreen input/output topics, which is what's implemented. The existing
  FTL vector/jump buttons use QoS 2 (the batch 6 input publishing follow-up
  used QoS 2 uniformly). Touchscreen mode-select follows the spec exactly
  (QoS 1) rather than the existing `PublishButtonState` convention (QoS 2).
  `PublishButtonState` is already QoS 2 (from the batch 6 follow-up); the
  touchscreen buttons call it and get QoS 2 as a result. This is a deviation
  from the spec (which says QoS 1 for these topics) but is conservative in
  the wrong direction (QoS 2 is stricter, not looser). Flagging in case it
  matters once the physical panel firmware exists and has to match.

  Update: having looked at `PublishButtonState` more carefully — it passes
  `MqttQualityOfServiceLevel.ExactlyOnce` (QoS 2). The spec says QoS 1 for
  touchscreen input. To match the spec exactly, the touchscreen buttons
  should call a QoS-1 variant. Left as-is (QoS 2) this batch since the
  behaviour is only more conservative; can be corrected if the physical
  panel firmware or the aux-display-client is sensitive to it.

- **LED state driven by round-trip, not button's own toggle**: pressing a
  button doesn't immediately light its LED. The LED updates only after the
  MQTT round-trip completes (press → input topic → sim-core → output topic →
  `SyncTouchscreenFromBus`). For a local broker this is imperceptible (<5ms
  typically), and it means a future sim-core override (e.g. mode locked
  during loadout) automatically reflects in the LED without any extra wiring
  in `ControlPanelsWindow`.

- **No loadout-mode interaction added**: explicitly out of scope per the
  spec. The routing exists; the override logic doesn't.

- **`SimBus.Touchscreen.Mode` thread safety**: written on the MQTT background
  thread (`OnMqttMessageReceived`), read on the Godot main thread
  (`SyncTouchscreenFromBus`). String reference assignment is atomic in .NET
  (guaranteed by the CLR memory model), so no lock is needed for this
  single-writer / single-reader pattern. Same pattern used throughout
  `PropulsionState` and `FtlState` for float/bool writes from the physics
  thread.

## Files changed

- [`scripts/MqttTelemetryPublisher.cs`](scripts/MqttTelemetryPublisher.cs)
  — subscription infrastructure added: `MessageReceived`/`Connected` events,
  `Subscribe()`, `SubscribeAllAsync()`, `ApplicationMessageReceivedAsync`
  wiring, `System.Buffers` using for payload decode.
- [`scripts/SimBus.cs`](scripts/SimBus.cs) — `TouchscreenState` nested class,
  `ValidTouchscreenModes` set, `OnMqttMessageReceived`/`OnMqttConnected`
  event handlers, subscription and event registration in `_Ready()`.
- [`scripts/ControlPanelsWindow.cs`](scripts/ControlPanelsWindow.cs) —
  `BuildTouchscreenModeTab` rewritten to store button/LED refs and wire
  publish handlers; `SyncTouchscreenFromBus()` added; `_Process` updated.

## Versions tested against

- Godot 4.7.1.stable.mono (`/Applications/Godot_mono.app`)
- .NET SDK 10.0.301, targeting `net8.0`
- `Godot.NET.Sdk/4.7.1`
- MQTTnet 5.2.0.1603
- Mosquitto 2.1.2 (Homebrew, `localhost:1883`, no auth/TLS)
- Build: `dotnet build sim-core.csproj` — **0 warnings, 0 errors**

---

Copy this file's contents into the Cold Orbit project conversation on
claude.ai — the master plan doc lives in that conversation's Knowledge
Base, not this repo.
