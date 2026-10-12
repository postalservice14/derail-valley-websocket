# Protocol v2: state + commands

Endpoint: `ws://127.0.0.1:9450/dv` (port is the UMM `Port` setting). Every message is JSON:

```json
{ "type": "<messageType>", "payload": { ... } }
```

v2 messages are camelCase. Clients that never send `hello` keep receiving the original v1 var/event
protocol (PascalCase) described in the README, so existing v1 clients are unaffected.

## Handshake

```json
{ "type": "hello", "payload": { "client": "streamdeck", "protocol": 2 } }
```

The mod replies with a `state` straight away, then sends `state` whenever anything changes (checked at the
`Rate` setting, 20 Hz by default) and at least once per second as a heartbeat. If no message arrives for
about 3 s, treat the connection as dead and reconnect.

## `state` (mod → client)

Any field can be `null`, meaning not available on this loco or not right now. `null` never means an error.
`loco` is `null` whenever the player isn't in a loco or its tender.

```jsonc
{
  "protocol": 2,
  "carName": "LocoShunter LocoDE2",          // the car the player is in (any car); null on foot
  "loco": {
    "id": "L-012", "liveryId": "LocoDE2",
    "kind": "diesel",                         // "steam" | "diesel" (diesel = anything else with cab controls, incl. electric)
    "speedKph": 42.1, "speedometerKph": 42.0,
    "wheelslip": 0.0, "isWheelslipping": false,
    "controls": {                             // lever positions, the same 0..1 scale `set` takes
      "throttle":         { "value": 0.5, "notches": 8, "blocked": false },
      "trainBrake":       { ... }, "independentBrake": { ... }, "dynamicBrake": { ... },
      "reverser":         { ... },            // 0.5 = neutral (on steam this is the cutoff lever)
      "horn": { ... }, "sander": { ... },
      "headlightsFront": { ... }, "headlightsRear": { ... },   // 0.4 = off
      "cabLight": { ... }
    },
    "brakes":    { "brakePipe": 5.0, "mainReservoir": 8.1, "brakeCylinder": 0.0 },  // gauge bar, as on the cab gauges
    "steam":     { "boilerPressure": 13.2, "boilerWaterLevel": 0.72, "safetyValve": 0,
                   "regulator": 0.3, "cutoff": 0.45 },        // cutoff signed -1..1; null on diesel
    "resources": { "fuel": 4000, "fuelMax": 6000, "sand": 180, "sandMax": 200, "oil": 90, "oilMax": 100 },
    "engine":    { "on": true, "rpm": 900, "rpmMax": 2000, "tractionMotorAmps": 400, "tractionMotorAmpsMax": 1000, "temperature": 60 },
    "lamps":     { "fuel": 0, "oil": 0, "sand": 0, "sander": 1, "headlightFront": 1, "headlightRear": 0, "cabLight": 0 }
  },
  "jobs": [ { "id": "FH-SM-01", "type": "ShuntingLoad", "state": "InProgress",
              "origin": "FH", "destination": "SM", "wage": 1200, "timeLimit": 0, "timeOnJob": 30 } ]
}
```

`notches` is non-null for notched levers and is the number of positions: a lever rests on `k / (notches - 1)`
(a 3-notch reverser is 0, 0.5, 1). The mod snaps every `set` and `nudge` to a notch, so read the
resulting `value` back instead of assuming the value you sent.

## `command` (client → mod)

```json
{ "type": "command", "payload": { "id": "42", "control": "throttle", "action": "nudge", "value": 0.05 } }
```

| action    | value                         | applies to |
| --------- | ----------------------------- | ---------- |
| `set`     | absolute lever position 0..1  | any lever; horn intensity 0..1 |
| `nudge`   | signed delta (e.g. `-0.05`)   | any lever; a notched lever moves at least one notch |
| `toggle`  | none                          | `sander`, `cabLight`, `headlights` (off ↔ last on position) |
| `press`   | optional, default 1           | `horn` (held until `release`), or momentary `sander` |
| `release` | none                          | `horn`, `sander` |

Controls: `throttle`, `trainBrake`, `independentBrake`, `dynamicBrake`, `reverser`, `horn`, `sander`,
`headlights` (= `headlightsFront`), `headlightsRear`, `cabLight`. Steam-only aliases: `regulator`
(= throttle) and `cutoff` (= reverser). On a diesel they're rejected so plugin mistakes show up. Names are
case-insensitive, and `_`/`-` are ignored (`train_brake` works).

Commands run on the game's main thread in the frame after they arrive. A held horn is released when its
client disconnects.

## `commandResult` (mod → client)

```json
{ "type": "commandResult", "payload": { "id": "42", "ok": true, "error": null, "value": 0.375 } }
```

`value` is the lever position after the command. Failures (`ok: false`) include "Not in a locomotive",
"This loco has no 'dynamicBrake' control", "'throttle' is blocked on this loco", and unknown controls or
actions.

## Other messages

- `event` (mod → all clients): `{ "name": "CAR_NAME_CHANGED", "value": "LocoShunter LocoDE2" | null }`
- `error` (mod → the sending client only): `{ "message": "..." }` for unparseable or unknown messages
