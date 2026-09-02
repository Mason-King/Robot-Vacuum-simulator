# Robot Vacuum Simulator

A capstone simulator built in three separable pieces: a headless Python
simulation engine, a WebSocket server that streams it in real time, and a
Vue 3 + canvas client, packaged by pywebview as a single desktop app.

```
engine/            simulation core - no I/O, no rendering, importable from tests
  house.py           Room / House rectangles, doorways, room-to-room routing
  obstruction.py     blocking and pass-under furniture
  floor.py           floor coverings: colour, cleaning efficiency, traction, slip
  coverage.py        per-cell coverage grid + reachability flood fill
  navigation.py      World: geometry queries and grid rebuilds
  physics.py         differential-drive chassis: motors, traction, bumper
  diagnostics.py     pre-run connectivity check and warning objects
  vacuum.py          the body: pose, drive limits, battery
  algorithms/        five controllers behind one base class
  simulation.py      Simulation: initialize_default / start / stop / step / get_state
server/            WebSocket wrapper: real-time loop, broadcast, JSON commands
client/            Vue 3 + Vite front end
  src/main.js        app entry
  src/App.vue        layout shell: header, canvas, side panel, warning dialog
  src/stores/        simulation.js (socket + reactive state), editor.js (tools, selection)
  src/lib/           framework-free helpers: geometry, coverage overlay, drive keys
  src/components/    FloorPlan.vue (canvas) + one panel per control group
app.py             pywebview launcher - spawns the server, serves the client, opens a window
prototype/         early pygame grid sketch, kept for reference
```

## Setup

```bash
pip install -r requirements.txt
cd client && npm install && npm run build
```

`app.py` serves `client/dist`, so the client must be built once before the
desktop app will start. It says so if you forget.

## Run

```bash
python app.py                # one native window, everything wired up
```

Or run the halves separately during development, with hot reload:

```bash
python -m server             # ws://127.0.0.1:8765
cd client && npm run dev     # http://127.0.0.1:5173
```

The client reads its socket URL from a `?ws=` query parameter and falls back to
`ws://127.0.0.1:8765`, which is what the dev server needs.

The engine needs neither - it is fully headless, which is how every number in
this file was measured:

```python
from engine import Simulation

sim = Simulation()                 # always boots into a valid default world
sim.set_algorithm("spiral")
sim.set_battery_capacity(20)
sim.start()
while sim.running:
    sim.step()                     # one fixed timestep, 1/30 s of simulated time
print(sim.get_state()["finalReport"])
```

## Using it

**Run.** Start / Stop / Reset, the algorithm picker, that algorithm's own
parameter sliders, and a time-scale slider for fast-forwarding a long run.

**Floor plan.** *Select* is the default tool: click a room or obstruction, drag
to move it, drag the blue corner grip to resize, `Delete` to remove, `Escape` to
cancel. Everything snaps to 0.1 m and the sidebar carries exact X/Y/W/H boxes.
*Draw room* turns a drag into a new room; *Place obstruction* drops one on a
click or sizes it on a drag. *Show grid* overlays the coverage grid - metre
majors always, individual cells once they are wide enough on screen to read.

**Manual driving.** Pick *Manual (WASD)*, press Start, and the keys name a
direction on the plan: `W` up, `S` down, `A` left, `D` right, two together for
the diagonal. Arrow keys work the same. The vacuum still has to turn to face
where it is going - a differential drive cannot slide sideways - so a small
course change arcs, while a reversal past the *move-off angle* pivots on the
spot first. It is an ordinary controller, so it cleans, drains the battery and
bumps into things exactly as an algorithm does.

## How the pieces fit

The engine imports nothing from the server and knows nothing about sockets. The
server steps it on a real-time accumulator loop and broadcasts; the client
draws. A layout change re-sends the whole world, everything else streams as
deltas.

| message | when | payload |
| --- | --- | --- |
| `world` | on connect, and whenever the layout changes | house, obstructions, grid, algorithms, coverings, limits, full coverage snapshot |
| `state` | every tick | pose, drive telemetry, battery, coverage stats, warnings, `coverageDelta` |
| `error` | rejected command | `message` |

`coverageDelta` is a flat `[cellIndex, percent, ...]` list of the cells that
changed since the previous frame, so a 9,600-cell grid streams in a few hundred
bytes instead of being resent thirty times a second.

Commands the other way:

```
{"command": "start"}                     {"command": "stop"}
{"command": "reset"}                     {"command": "recharge"}
{"command": "set_speed", "speed": 1.5, "unit": "m/s" | "ft/s"}
{"command": "set_battery_capacity", "minutes": 45}
{"command": "set_algorithm", "algorithm": "spiral"}
{"command": "set_algorithm_params", "params": {"lane_spacing": 0.45}}
{"command": "set_physics", "params": {"maxAccel": 1.2, "maxTurnRate": 3.0,
                                      "bumperDamping": 0.5, "wheelBase": 0.23}}
{"command": "set_drive", "x": 1, "y": -1}                      manual: up-right
{"command": "set_floor_covering", "covering": "low_pile"}      house default
{"command": "set_room_covering", "room": "kitchen", "covering": "tile" | null}
{"command": "set_efficiency", "efficiency": 0.7, "covering": "tile"}
{"command": "place_obstruction", "x": 5.6, "y": 1.4, "width": 0.8, "height": 1.9,
 "kind": "blocking" | "pass_under"}
{"command": "update_obstruction", "id": 3, "x": 5.0, "y": 1.0}
{"command": "remove_obstruction", "id": 3}
{"command": "add_room", "name": "porch", "x": 12, "y": 3, "width": 3, "height": 2.4}
{"command": "update_room", "name": "porch", "x": 12, "y": 4, "newName": "deck"}
{"command": "remove_room", "name": "porch"}
{"command": "set_time_scale", "scale": 8}
```

## The house

Rooms are axis-aligned rectangles in one coordinate space, and **doorways are
implicit**: two rooms connect wherever their rectangles overlap, and the centre
of that overlap is the waypoint between them. Rectangles are convex, so a
straight line from anywhere in a room to one of its doorways always stays inside
that room - which is what lets navigation route between rooms with no path
planner.

Drag a room clear of its neighbours and the connectivity check says so
immediately, naming the consequence: *"Room 'study' cannot be reached from the
vacuum's starting position - it will register 0% coverage (11.4 m² of floor will
never be cleaned)."* The engine never blocks on a warning; the client asks you
to acknowledge one before starting.

Room edits re-dimension the coverage grid, so they reset the run. Obstruction
edits do not - furniture can be rearranged mid-run.

## Floor coverings

Each room carries its own covering, or `null` to inherit the house default; pick
one from the room inspector after selecting a room. A covering decides two
things at once:

| Covering | Efficiency | Traction | Slip |
| --- | --- | --- | --- |
| Hardwood | 0.95 | 100% | none |
| Tile | 0.90 | 98% | 0.004 |
| Low-pile carpet | 0.65 | 90% | 0.030 |
| High-pile carpet | 0.40 | 76% | 0.090 |

`Simulation.covering_at` resolves the surface under the vacuum every tick, so
crossing a doorway from tile onto deep carpet changes what the wheels can do at
the moment it crosses. Efficiency is a property of the covering *type*, not of
the room: setting low-pile to 30% applies everywhere low-pile is laid.

## The drive model

The vacuum is a differential-drive body, not a point that teleports along a
heading. Each tick:

1. **Motors slew.** Commanded speed and turn rate are approached under
   acceleration limits (0.7 m/s² up, 1.4 m/s² braking, 9 rad/s² angular), so the
   body has momentum.
2. **Wheels resolve.** `v_left = v - ω·b/2`, `v_right = v + ω·b/2` over a 0.23 m
   wheel base; the turn radius is `v / ω`.
3. **The floor takes its cut**, per the traction and slip above.
4. **Rotate, then translate.** A disc is rotationally symmetric, so turning can
   never push it into a wall on its own.
5. **Contact resolves against a normal**, read off the rim directions that are
   blocked at the *rejected* position - probing where the vacuum still fits
   finds nothing blocked. Motion splits into the part pushing into the surface
   and the part running along it; the bumper absorbs `cos²(angle) × damping` of
   the speed and the rest slides. A square hit stops the vacuum dead; a 12°
   graze keeps 97% of its speed and runs along the wall.
6. **The battery pays for the work.** Draw is standby plus terms for speed and
   turn rate. Over 60 simulated seconds on a 10-minute pack: parked leaves
   97.0%, driving 95.1%, driving while turning 88.5%.

Contact is **edge-triggered**: a controller sees `contact` on the tick of impact,
not on every tick spent leaning on a wall. Without that, backing away brushes the
wall again, reads as a fresh hit, and restarts the escape on top of itself.

## Algorithms

Every algorithm is a *controller*: `update()` returns a `DriveCommand(speed,
turn)` and never touches a position. Each declares its knobs in a `PARAMS` dict
and the client builds sliders from the schema the engine sends, so adding a
parameter needs no client change. Values are clamped server-side and remembered
per algorithm.

| Algorithm | Parameters | Covered in 10 min | Bumps |
| --- | --- | --- | --- |
| Snaking | lane spacing | 74.7% | 9 |
| Random bounce | shortest / longest leg, bounce scatter | 54.9% | 42 |
| Wall following | wall standoff, steering gain | 33.8% | 132 |
| Spiral | ring spacing, restart radius, relocate time | 16.7% | 165 |
| Manual | turn rate, move-off angle | — | — |

They all drive roughly the same distance on one battery. What separates them is
how much of it lands on floor they have not already cleaned.

Two behaviours worth knowing:

- **Snaking plans against reachability.** Each room's lanes are clipped to the
  part the vacuum can actually get to (one flood fill per room), so it never
  drives at a lane end sealed off by furniture. It abandons a target after a
  second of no *progress* toward it - testing for movement instead livelocks,
  because sliding along an obstacle is movement.
- **The spiral is honest.** It holds a constant speed and sets turn rate to
  `v / r` with `r` growing, which *is* an Archimedean spiral rather than a chase
  along one. Its coverage scales with how far it relocates between spirals
  (12.5% at 4 s of travel, 19.7% at 6 s, 24.6% at 8 s), because the rings
  themselves only ever cover one small disc: locally thorough, globally poor.

Parameters have real teeth. Lane spacing on a 20-minute battery:

| Lane spacing | Outcome | Covered | Fully cleaned | Driven |
| --- | --- | --- | --- | --- |
| 0.15 m | ran flat | 80.6% | 58.6% | 350 m |
| 0.30 m | finished the house | 90.0% | 24.2% | 227 m |
| 0.60 m | finished the house | 65.2% | 8.6% | 125 m |

The chassis is tunable too, from the **Chassis** panel or `set_physics`:
acceleration, maximum turn rate, bumper damping, wheel base.

## Coverage vs cleanliness

A cell accumulates `efficiency` per pass, so three numbers get reported:
`percent` (visited at least once - the headline coverage figure),
`cleanedPercent` (reached 100%), and `meanCleanliness`. High-pile carpet at 0.40
needs about three passes per cell, which is why a single sweep over mixed floors
can visit 75% of the house while fully cleaning far less of it.

Blocking obstructions are cut out of the denominator entirely: their area is
reported separately as `nonCleanableArea` and never counts against coverage.

## Requirements

| Requirement | Where |
| --- | --- |
| 1.1.2 valid default state on boot | `Simulation.initialize_default` |
| 1.2.1 / 1.2.3 rooms in a shared 2D space | `engine/house.py` |
| 1.2.4 / 1.2.5 connectivity check + consequence message | `engine/diagnostics.py`, `WarningDialog.vue` |
| 1.3.2 / 1.3.4 battery drains with sim time, configurable capacity | `engine/vacuum.py` |
| 1.3.3 zero battery keeps the session editable | `Simulation.step`, `Simulation.recharge` |
| 1.3.5 speed in m/s and ft/s | `Vacuum.to_dict`, `VacuumPanel.vue` |
| 1.3.6 constant speed during a run | `Simulation.set_speed` queues changes |
| 1.4.1 / 1.4.2 start / stop | `Simulation.start` / `.stop` |
| 1.5.1 render house, rooms, vacuum, obstructions | `client/src/components/FloorPlan.vue` |
| 1.5.2 / 1.5.3 coverings, visually distinct | `engine/floor.py`, `floorFill()` |
| 1.6.1 four algorithms behind one interface (five with manual) | `engine/algorithms/` |
| 1.7.1-1.7.4 blocking and pass-under obstructions | `engine/obstruction.py` |
| 1.7.5 / 1.7.6 blocked area excluded from totals | `CoverageGrid.rebuild` / `.stats` |
| 1.7.7 / 1.7.8 efficiency per covering, bounded | `Simulation.step`, `clamp_efficiency` |
| 1.7.9 per-cell state each step | `CoverageGrid.apply` |
| 1.7.10 final coverage percentage | `Simulation._report` |
| 1.7.11 live colour-coded overlay | `client/src/lib/coverageOverlay.js` |
| 2.2 fixed timestep (built early on purpose) | `Simulation.step` |
| 2.3 multiple algorithms | `engine/algorithms/__init__.py` |
| Beyond Tier 1: per-room coverings | `Room.covering`, `Simulation.covering_at` |
| Beyond Tier 1: room editing | `Simulation.add_room` / `update_room` / `remove_room` |
| Beyond Tier 1: algorithm + chassis tuning | `MovementAlgorithm.PARAMS`, `Simulation.set_physics` |

## Design notes

- **The client keeps its logic out of its components.** Geometry, the coverage
  overlay and the WASD key mapping are plain ES modules under `src/lib/`, so they
  can be exercised straight from Node without a browser or a test harness.
- **State lives in `src/stores/`, not in props.** `simulation.js` owns the socket
  and mirrors engine state; `editor.js` owns tool and selection, which the canvas
  and the side panel both read. Swapping either for a Pinia store would not
  change a single component.
- **Per-cell coverage stays outside the reactive proxy.** Thousands of numbers
  churning through a deep proxy every tick is pure waste; components watch
  `state.tick` and the overlay applies the delta.
- **Nothing stops the vacuum permanently.** A blocked move slides along the
  surface, and `Simulation.step` runs a reverse-and-turn recovery after a second
  of no movement. It only stays put if it is sealed in on every side - and then
  the connectivity warning already says why.
