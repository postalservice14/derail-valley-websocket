# Stream Deck+ integration plan

## Decision: keep the shell, rewrite the core

The upstream mod is about 600 lines. The parts worth keeping are the shell: UMM entry point and settings,
build and packaging scripts, the websocket-sharp server, the `{type, payload}` envelope, the
speedometer helper, and the car-changed event. Those are kept unchanged or lightly fixed, and the original
v1 var protocol still works for existing clients.

The data and control core was replaced rather than extended, because two-way control breaks its
assumptions:

1. **Threading.** Upstream handles messages on websocket-sharp's thread pool. Subscribing already
   touches Unity objects (`PlayerManager.Car`) off the main thread and mutates a dictionary the main thread
   iterates. Commands would make that write-heavy, so every inbound message now goes through a main-thread
   queue (`MainThread.cs`) that `UpdateDriver` drains each frame.
2. **Reflection var registry.** Vars are string paths re-resolved through reflection on every tick. Almost
   everything new (finding the `Boiler` sim component, tender-to-loco resolution, gauge offsets, notch
   info) needs real code anyway, so the registry would only wrap static helpers. A typed snapshot
   (`StateReader.cs`) is cheaper, easier to read, and gives the plugin a schema.
3. **Failure isolation.** Upstream throws when not in a car, which dropped *every* var for that frame
   and logged at 20 Hz. The snapshot reads each section independently, and missing data is `null`.
4. **Push-everything fits a deck better than per-var subscriptions.** The plugin needs nearly all fields,
   and a stateless "hello, then full snapshots" design makes reconnecting trivial.
5. **Write path.** Upstream reads levers via `HUDInterfacer`. The game's own remote control writes through
   `SimController.controlsOverrider` (`BaseControlsOverrider`), so both reads and writes now go through
   it, with notch awareness and blocked-control checks.

Bugs fixed along the way: the `reverser` var was emitted as `train_brake`, errors were broadcast to every
client, unsubscribe was ignored, and main-thread sends were blocking.

## Phase 1: mod + test client (done)

- `PROTOCOL.md` is the contract.
- Steam: the game's "regulator" is `ControlType.Throttle` and "cutoff" is `ControlType.Reverser`
  (the game tutorial labels them that way). The mod accepts `regulator`/`cutoff` as steam-only aliases.
- Pressures are reported as gauge bar (absolute − 1), matching the in-cab gauges.
- Jobs are read via `JobsManager`'s backing field, because `JobsManager.Instance` auto-creates the
  singleton in the main menu.
- `test-client/`: `npm run selftest` (scripted read/write check, restores levers afterwards),
  `npm start` (interactive), `npm run watch`, and `mock-server.js` (fake mod for working without the game).

### To verify in-game

1. Reinstall Unity Mod Manager for Derail Valley (it isn't installed right now).
2. Quit the game and run `.\deploy.ps1`.
3. Start the game, sit in a stationary DE2, then `cd test-client && npm run selftest`. Repeat in an S282 or
   S060 and a DE6 or DH4 (notched throttle, dynamic brake).
4. Check by hand: the levers visibly move, the horn sounds and stops, headlight toggle turns lights off/on,
   walking out of the cab makes `loco` null with no log spam, and the main menu doesn't error.

## Phase 2: Stream Deck+ plugin (next)

Stack: TypeScript, `@elgato/streamdeck` v2, `@elgato/cli`, Rollup, `ws`. **Requires Node 20+** for
tooling (this machine has Node 14). The Stream Deck app runs the plugin on its own bundled Node.

- **Connection singleton**: a TS port of `test-client/dv-connection.js` (hello on open, 3 s stale detection,
  backoff from 0.5 to 5 s, command correlation by id). All actions subscribe to it. Disconnected or not in a
  loco shows a dimmed "—" state.
- **Dials (encoders)**: actions for throttle/regulator, train brake, independent brake, dynamic brake, and
  reverser/cutoff. Rotate sends `nudge` (ticks × step, coalesced to one command every 50 ms). Press does
  something per dial (independent brake: full release/apply toggle; throttle: idle). Touch tap is
  configurable. The touch strip uses a custom `$B1`-style layout: lever bar + value + one readout
  (e.g. train brake shows BP/BC bar, throttle shows speed or amps).
- **Optimistic UI**: show the dialled value immediately, then follow `state` once the mod echoes it, so
  turning doesn't fight 20 Hz updates.
- **Keys**: speed, brake pipe, main reservoir, fuel, sand, boiler pressure, water level, wheelslip, job.
  Each is an SVG rendered per state, with thresholds (e.g. MR < 5 bar amber, < 3 red; sand < 15% amber;
  water < 0.3 or > 0.9 red), configurable in the property inspector. Changes are throttled to ≤ 10 Hz per
  key. Horn (keyDown press / keyUp release), sander toggle, headlights toggle, cab light toggle.
- **Diesel vs steam**: one profile per kind (dials: throttle | train brake | independent | dynamic on
  diesel; regulator | cutoff | train brake | independent on steam). The plugin calls
  `switchToProfile` when `loco.kind` changes. Each action also adapts on its own, so a single
  hand-built layout still works (a dynamic-brake dial on a steam loco shows "n/a").
- **Testing**: unit tests for the threshold/format/coalescing logic, then dev against `mock-server.js`
  (`--steam` for steam layout), then in-game.
