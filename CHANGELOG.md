# 2.0.0

- added protocol v2 for two-way control: send `hello` to receive `state` snapshots (on change + 1 s heartbeat) and send `command`s; see PROTOCOL.md
- added telemetry: independent/dynamic brake, brake pipe, main reservoir and cylinder pressure, steam boiler pressure, water level, regulator and cutoff, wheelslip, lamps, resources, current jobs
- added commands: set/nudge throttle, train brake, independent brake, dynamic brake, reverser, regulator, cutoff; horn press/release (auto-released on disconnect); sander, headlights and cab light toggle
- incoming messages are now handled on Unity's main thread (previously read game objects from WebSocket threads)
- fixed `reverser` var being emitted as `train_brake`
- fixed every var going missing for a frame when one var's target is unavailable (e.g. `car_speed` while on foot)
- errors are now sent only to the client that caused them
- added `UnsubscribeFromVar` and `UnsubscribeFromEvent` handling
- sends are now asynchronous so a slow client cannot stall the game

# 1.4.0

- replaced individual var messages with a single one for performance
- added `Rate` setting to slow down emission (default is 20hz)

# 1.3.0

- added standard simport support:\
   var: `port_EngineRPM`\
   unit: `number`
- renamed display name

# 1.2.0

- added var `train_brake` (`position`)

# 1.1.0

- added var `throttle` (`position`)

# 1.0.3

- add car type into name
- added port setting

# 1.0.2

- fixed emit pausing on loading a new game
- fixed crash on return to main menu
- fixed `System.InvalidOperationException: Collection was modified; enumeration operation may not execute.` error
- improved WebSocket error handling and logging

# 1.0.1

- changed car speedo to use UI if available, otherwise use port, otherwise use absolute speed

# 1.0.0

Initial version
