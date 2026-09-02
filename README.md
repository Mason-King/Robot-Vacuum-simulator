# Robot Vacuum Simulator

Capstone MVP: a headless Python simulation engine, a WebSocket server that
streams its state, and a Vue + canvas client wrapped in a native window.

```
engine/            simulation core - no I/O, no rendering, importable from tests
  house.py           Room / House rectangles, doorways, room-to-room routing
  obstruction.py     blocking and pass-under furniture
  floor.py           floor coverings and their cleaning efficiency
  coverage.py        per-cell coverage grid + reachability flood fill
  navigation.py      World: geometry queries and grid rebuilds
  physics.py         differential-drive chassis: motors, traction, bumper
  diagnostics.py     pre-run connectivity check and warning objects
  vacuum.py          vacuum entity + battery
  algorithms/        snaking, wall_following, random, spiral behind one base class
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

The engine needs neither - it is fully headless:

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

## Protocol

Server to client:

| message | when | payload |
| --- | --- | --- |
| `world` | on connect, and whenever the layout changes | house, obstructions, grid, algorithms, coverings, limits, full coverage snapshot |
| `state` | every tick | pose, battery, coverage stats, warnings, `coverageDelta` |
| `error` | rejected command | `message` |

`coverageDelta` is a flat `[cellIndex, percent, ...]` list of cells that changed
since the previous frame, so a 9600-cell grid streams in a few hundred bytes.

Client to server:

```
{"command": "start"}                     {"command": "stop"}
{"command": "reset"}                     {"command": "recharge"}
{"command": "set_speed", "speed": 1.5, "unit": "m/s" | "ft/s"}
{"command": "set_battery_capacity", "minutes": 45}
{"command": "set_algorithm", "algorithm": "spiral"}
{"command": "set_floor_covering", "covering": "low_pile"}      house default
{"command": "set_room_covering", "room": "kitchen", "covering": "tile" | null}
{"command": "set_efficiency", "efficiency": 0.7, "covering": "tile"}
{"command": "place_obstruction", "x": 5.6, "y": 1.4, "width": 0.8, "height": 1.9,
 "kind": "blocking" | "pass_under"}
{"command": "update_obstruction", "id": 3, "x": 5.0, "y": 1.0}
{"command": "remove_obstruction", "id": 3}
{"command": "set_algorithm_params", "params": {"lane_spacing": 0.45}}
{"command": "add_room", "name": "porch", "x": 12, "y": 3, "width": 3, "height": 2.4}
{"command": "update_room", "name": "porch", "x": 12, "y": 4, "newName": "deck"}
{"command": "remove_room", "name": "porch"}
{"command": "set_time_scale", "scale": 8}
```

## Floor coverings

Each room carries its own covering, or `null` to inherit the house default -
pick one from the room inspector after selecting a room on the plan. The
covering decides two things at once: cleaning efficiency (how many passes a
cell needs) and traction (how much of the commanded motion reaches the floor,
and how much the wheels drift). `Simulation.covering_at` resolves the surface
under the vacuum every tick, so crossing a doorway from tile onto deep carpet
changes what the wheels can do at the moment it crosses.

Efficiency is a property of the covering *type*, not of the room: setting
low-pile carpet to 30% applies everywhere low-pile carpet is laid. The slider
edits whichever covering you are looking at - the selected room's, or the house
default when nothing is selected.

## Editing the floor plan

Select is the default tool: click a room or obstruction to select it, drag to
move, drag the blue corner grip to resize, `Delete` to remove, `Escape` to
cancel. Everything snaps to 0.1 m, and the sidebar shows exact X/Y/W/H boxes
for the selection. **Show grid** overlays the coverage grid - metre majors
always, individual cells once they are wide enough on screen to read. **Draw room** turns a drag into a new room; **Place
obstruction** drops one at the configured size on a click, or sizes it on a
drag.

Doorways are implicit: two rooms are connected wherever their rectangles
overlap. Drag a room clear of its neighbours and the connectivity check
immediately reports it as unreachable, with the floor area that will register
0% coverage.

Room edits re-dimension the coverage grid, so they reset the run. Obstruction
edits do not - you can rearrange furniture mid-run.

## Tuning algorithms

Each algorithm declares its knobs in a `PARAMS` dict, and the client builds
sliders from the schema the engine sends - adding a parameter needs no client
change. Values are clamped server-side and remembered per algorithm, so
switching away and back keeps your settings.

| algorithm | parameters |
| --- | --- |
| snaking | lane spacing (0.10-1.00 m) |
| wall following | turn rate, hug rate (radians) |
| random | shortest / longest leg (seconds) |
| spiral | ring spacing, restart radius (m) |
| manual | turn rate (rad/s), reverse speed |

The chassis itself is tunable too, from the **Chassis** panel or the
`set_physics` command: acceleration, maximum turn rate, bumper damping and
wheel base.

**Manual drive.** Pick *Manual (WASD)* and press Start, then **W**/**S** to
drive and reverse, **A**/**D** to turn (arrow keys work too). It is an ordinary
movement algorithm, so it cleans, drains the battery and collides like any
other - it just takes its heading from the keyboard. Releasing the keys parks
the vacuum: manual mode sets `self_driving = False`, which exempts it from the
anti-stall nudge below.

Lane spacing on a 20-minute battery, for example: 0.15 m covers 72.8% before
going flat, 0.30 m finishes the house at 95.3%, 0.60 m races through at 71.6%.

## Tier 1 coverage

| Requirement | Where |
| --- | --- |
| 1.1.2 valid default state on boot | `Simulation.initialize_default` |
| 1.2.1 / 1.2.3 rooms in a shared 2D space | `engine/house.py` |
| 1.2.4 / 1.2.5 connectivity check + consequence message | `engine/diagnostics.py`, client modal |
| 1.3.2 / 1.3.4 battery drains with sim time, configurable capacity | `engine/vacuum.py` |
| 1.3.3 zero battery keeps the session editable | `Simulation.step`, `recharge` |
| 1.3.5 speed in m/s and ft/s | `Vacuum.to_dict`, client unit toggle |
| 1.3.6 constant speed during a run | `Simulation.set_speed` queues changes |
| 1.4.1 / 1.4.2 start / stop | `Simulation.start` / `.stop` |
| 1.5.1 render house, rooms, vacuum, obstructions | `client/app.js` `draw()` |
| 1.5.2 / 1.5.3 coverings, visually distinct | `engine/floor.py`, `floorFill()` |
| per-room floor coverings | `Room.covering`, `Simulation.covering_at` |
| 1.6.1 four algorithms behind one interface (five with manual) | `engine/algorithms/` |
| room editing (add / move / resize / rename / delete) | `Simulation.add_room` etc., client canvas tools |
| algorithm parameter tuning | `MovementAlgorithm.PARAMS`, `set_algorithm_params` |
| 1.7.1-1.7.4 blocking and pass-under obstructions | `engine/obstruction.py` |
| 1.7.5 / 1.7.6 blocked area excluded from totals | `CoverageGrid.rebuild` / `.stats` |
| 1.7.7 / 1.7.8 efficiency per covering, bounded | `Simulation.step`, `clamp_efficiency` |
| 1.7.9 per-cell state each step | `CoverageGrid.apply` |
| 1.7.10 final coverage percentage | `Simulation._report` |
| 1.7.11 live colour-coded overlay | `client/app.js` overlay canvas |
| 2.2 fixed timestep (built early on purpose) | `Simulation.step` |
| 2.3 multiple algorithms | `engine/algorithms/__init__.py` |

## The drive model

The vacuum is a differential-drive body, not a point that teleports along a
heading. Each tick:

1. **Motors slew.** Commanded speed and turn rate are approached under
   acceleration limits (0.7 m/s^2 up, 1.4 m/s^2 braking, 9 rad/s^2 angular), so
   the body has momentum.
2. **Wheels resolve.** `v_left = v - w*b/2`, `v_right = v + w*b/2` over a
   0.23 m wheel base; the turn radius is `v / w`.
3. **The floor takes its cut.** Each covering has a traction factor and a slip
   term - high-pile carpet delivers 76% of commanded motion and drifts a few
   degrees per run; hardwood delivers all of it and drifts none.
4. **Rotate, then translate.** A disc is rotationally symmetric, so turning can
   never push it into a wall on its own.
5. **Contact resolves against a normal.** The normal is read off the rim
   directions that are blocked at the rejected position. Motion splits into the
   part pushing into the surface and the part running along it: the bumper
   absorbs `cos^2(angle) * damping` of the speed, and the rest slides. A square
   hit stops the vacuum; a 12-degree graze keeps 97% of its speed and runs along
   the wall.
6. **The battery pays for the work.** Draw is standby plus terms for speed and
   turn rate, so a parked vacuum outlasts a driving one and turning costs most.

Algorithms are controllers: `update()` returns a `DriveCommand(speed, turn)`
and never touches a position. That is what makes the spiral honest - it holds a
constant speed and sets turn rate to `v / r` with `r` growing, which *is* an
Archimedean spiral, rather than chasing points along one.

## Notes for the next tier

- **Coverage vs cleanliness.** A cell accumulates `efficiency` per pass, so
  three numbers are reported: `percent` (visited at least once - the headline
  coverage figure), `cleanedPercent` (reached 100%), and `meanCleanliness`.
  High-pile carpet at 0.4 efficiency needs about three passes per cell.
- **Doorways** are wherever two room rectangles overlap; the overlap centre is
  the waypoint. Rooms are convex, so a straight line from anywhere in a room to
  one of its doorways stays inside that room.
- **Movement** resolves collisions by sub-stepping and, for target-following
  algorithms, sliding along walls. Reactive algorithms (wall following, random,
  spiral) deliberately do not slide - they need the blocked signal to steer.
- **Snaking plans against reachability.** Each room's lanes are clipped to the
  part the vacuum can actually get to (one flood fill per room), so it never
  drives at a lane end sealed off by furniture.
- **Hitting something never stops the vacuum.** `World.escape` fans out from
  the current heading and takes the first direction with room in it; every
  algorithm calls it through `MovementAlgorithm.deflect`, and `Simulation.step`
  runs it as a last resort after a second of no movement. A vacuum only stays
  put if it is sealed in on all sides.
- **The client keeps its logic out of the components.** Geometry, the coverage
  overlay and the WASD key mapping are plain ES modules under `src/lib/`, so
  they can be exercised straight from Node without a browser or a test harness.
- **State lives in `src/stores/`, not in props.** `simulation.js` owns the
  socket and mirrors engine state; `editor.js` owns tool and selection, which
  the canvas and the side panel both read. Swapping either for a Pinia store
  would not change a single component.
- **Progress, not motion, decides when to give up on a target.** Snaking
  abandons a lane end after a second of not getting any closer to it - testing
  for movement instead would livelock, because deflecting *is* movement.
