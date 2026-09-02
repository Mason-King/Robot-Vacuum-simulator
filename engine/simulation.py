"""The simulation: a headless, fixed-timestep loop over the house.

Fixed timestep (Tier 2's 2.2) from day one - `step()` always advances exactly
`dt` simulated seconds, so a run is reproducible regardless of how fast the
machine driving it happens to be.
"""

import random

from .algorithms import ALGORITHMS, DEFAULT_ALGORITHM, describe_algorithms
from .diagnostics import Warning, check_connectivity
from .floor import (
    COVERINGS,
    DEFAULT_COVERING,
    EFFICIENCY_BOUNDS,
    clamp_efficiency,
    coverings_dict,
)
from .house import Room, default_house
from .navigation import World
from .physics import Chassis, DriveCommand
from .obstruction import KINDS, Obstruction, default_obstructions
from .vacuum import (
    ACCEL_BOUNDS,
    CAPACITY_BOUNDS_MIN,
    SPEED_BOUNDS_MS,
    TURN_BOUNDS,
    Battery,
    Vacuum,
    clamp,
    to_metres_per_second,
)

DEFAULT_DT = 1.0 / 30.0
ROOM_MIN_SIDE = 0.5  # a room smaller than the vacuum cannot be swept
STALL_TICKS = 30  # a second of no movement triggers the last-resort unstick
RECOVERY_TICKS = 18  # how long that reverse-and-turn manoeuvre runs


class Simulation:
    def __init__(self, dt: float = DEFAULT_DT, seed: int | None = None):
        self.dt = dt
        self.seed = seed
        self.initialize_default()

    # -- boot (1.1.2) -------------------------------------------------------
    def initialize_default(self) -> None:
        """Build a complete, valid default world. The app always boots into this."""
        self.world = World(default_house(), default_obstructions())
        self.chassis = Chassis(random.Random(self.seed))
        self.covering = COVERINGS[DEFAULT_COVERING]  # house default (1.5.2)
        self.efficiencies = {
            name: covering.default_efficiency for name, covering in COVERINGS.items()
        }
        self.algorithm_name = DEFAULT_ALGORITHM
        self.algorithm_params: dict[str, dict] = {}
        self.vacuum = Vacuum(battery=Battery())
        self.vacuum.x, self.vacuum.y = self._spawn_point()
        self._apply_surface()
        self.controller = ALGORITHMS[self.algorithm_name](self.world, self.seed)
        self.controller.reset(self.vacuum)
        self._reset_run_state()
        self.refresh_warnings()

    def _reset_run_state(self) -> None:
        self.tick = 0
        self.sim_time = 0.0
        self.running = False
        self.status = "idle"
        self.pending_speed: float | None = None
        self.final_report: dict | None = None
        self.notice: str | None = None
        self.stalled_ticks = 0
        self.recovery_ticks = 0

    def _spawn_point(self) -> tuple[float, float]:
        room = self.world.house.rooms[0]
        margin = self.vacuum.radius + 0.06
        candidate = (room.x + margin, room.y + margin)
        if self.world.is_free(*candidate):
            return candidate
        index = self.world.grid._nearest_passable(*candidate)
        return self.world.grid.centre(index) if index is not None else candidate

    # -- floor coverings ------------------------------------------------------
    def covering_at(self, x: float, y: float):
        """The covering under a point: the room's own, else the house default."""
        room = self.world.house.room_at(x, y)
        if room is not None and room.covering in COVERINGS:
            return COVERINGS[room.covering]
        return self.covering

    @property
    def efficiency(self) -> float:
        """Cleaning efficiency of the house default covering (1.7.7)."""
        return self.efficiencies[self.covering.name]

    def _apply_surface(self) -> None:
        """Push the covering under the wheels onto the drive model.

        Called every tick, so crossing a doorway from tile onto deep carpet
        changes what the wheels can do at the moment the vacuum crosses it.
        """
        surface = self.covering_at(self.vacuum.x, self.vacuum.y)
        self.vacuum.surface.traction = surface.traction
        self.vacuum.surface.slip = surface.slip

    def reset(self) -> None:
        """Clear the run - keeps the house, obstructions and settings."""
        self.world.grid.rebuild(
            in_house=self.world.in_house,
            cleanable=self.world.is_cleanable,
            passable=lambda x, y: self.world.is_free(x, y),
            keep_progress=False,
        )
        self.vacuum.x, self.vacuum.y = self._spawn_point()
        self.vacuum.heading = 0.0
        self.vacuum.distance_travelled = 0.0
        self.vacuum.bumps = 0
        self.vacuum.halt()
        self.vacuum.battery.recharge()
        self.controller.reset(self.vacuum)
        self._reset_run_state()
        self.refresh_warnings()

    # -- control (1.4.1, 1.4.2) ---------------------------------------------
    def start(self) -> list[Warning]:
        """Begin (or resume) a run. Warnings are reported, never enforced."""
        self.refresh_warnings()
        if self.vacuum.battery.empty:
            self.status = "battery-empty"
            self.notice = "Battery is empty - recharge or raise capacity to run again."
            return self.warnings

        if self.tick == 0:
            self.controller.reset(self.vacuum)
        if self.pending_speed is not None:
            self.vacuum.set_speed(self.pending_speed)
            self.pending_speed = None

        self.running = True
        self.status = "running"
        self.final_report = None
        self.notice = None
        return self.warnings

    def stop(self) -> None:
        if self.running:
            self.running = False
            self.vacuum.halt()
            self.status = "stopped"
            self.final_report = self._report("stopped")

    # -- the loop -----------------------------------------------------------
    def step(self) -> dict:
        """Advance one fixed timestep. A no-op while stopped."""
        if not self.running:
            return self.get_state()

        self._apply_surface()

        # The controller only asks; the chassis decides what the motors, the
        # floor and the walls actually allow.
        command = self.controller.update(self.vacuum, self.dt)
        if self.recovery_ticks > 0:
            self.recovery_ticks -= 1
            command = DriveCommand(-self.vacuum.speed * 0.5, self.vacuum.limits.max_turn_rate)

        before = self.vacuum.distance_travelled
        contact = self.chassis.integrate(self.world, self.vacuum, command, self.dt)
        moved = self.vacuum.distance_travelled - before

        # A bumper is edge-triggered: controllers react to the moment of impact,
        # not to every tick spent leaning on the same wall.
        touching = contact is not None
        self.vacuum.contact = contact if touching and not self.vacuum.touching else None
        if self.vacuum.contact is not None:
            self.vacuum.bumps += 1
        self.vacuum.touching = touching

        # Safety net: whatever the algorithm was trying to do, a vacuum that has
        # not moved for a second reverses and swings away. It only stays put if
        # it is genuinely sealed in on every side.
        if moved <= 1e-9 and self.controller.self_driving:
            self.stalled_ticks += 1
            if self.stalled_ticks >= STALL_TICKS:
                self.recovery_ticks = RECOVERY_TICKS
                self.stalled_ticks = 0
        else:
            self.stalled_ticks = 0

        # Dose is normalised by the brush width, so one complete pass over a
        # cell deposits roughly `efficiency` worth of cleaning whatever the
        # speed or the grid resolution: hardwood is done in a single pass,
        # high-pile carpet needs two or three (1.7.7).
        if moved > 0:
            local = self.efficiencies[self.covering_at(self.vacuum.x, self.vacuum.y).name]
            dose = local * moved / (2 * self.vacuum.clean_radius)
            self.world.grid.apply(
                self.vacuum.x, self.vacuum.y, self.vacuum.clean_radius, dose
            )

        self.vacuum.battery.drain(self.dt, self.vacuum.motor_load)
        self.tick += 1
        self.sim_time += self.dt

        # Battery death parks the vacuum but leaves the session intact (1.3.3).
        if self.vacuum.battery.empty:
            self.running = False
            self.vacuum.halt()
            self.status = "battery-empty"
            self.notice = "Battery empty - the vacuum stopped. Settings stay editable."
            self.final_report = self._report("battery-empty")
        elif self.controller.done:
            self.running = False
            self.vacuum.halt()
            self.status = "finished"
            self.final_report = self._report("finished")

        return self.get_state()

    # -- settings ------------------------------------------------------------
    def set_speed(self, value: float, unit: str = "m/s") -> dict:
        """Speed is held constant for the duration of a run (1.3.6)."""
        if self.running:
            self.pending_speed = to_metres_per_second(value, unit)
            self.notice = (
                f"Speed change queued: {self.pending_speed:.2f} m/s applies at the "
                "next start (speed is constant during a run)."
            )
        else:
            self.vacuum.set_speed(value, unit)
            self.notice = None
        return {"speed": self.vacuum.speed, "pending": self.pending_speed}

    def set_battery_capacity(self, minutes: float) -> None:
        self.vacuum.battery.set_capacity(minutes)

    def recharge(self) -> None:
        self.vacuum.battery.recharge()
        if self.status == "battery-empty":
            self.status = "stopped" if self.tick else "idle"
            self.notice = None

    def set_algorithm(self, name: str) -> None:
        if name not in ALGORITHMS:
            raise ValueError(f"unknown algorithm {name!r}")
        self.algorithm_name = name
        self.controller = ALGORITHMS[name](
            self.world, self.seed, self.algorithm_params.get(name)
        )
        self.controller.reset(self.vacuum)

    def set_algorithm_params(self, values: dict) -> dict:
        """Tune the running algorithm. Values are clamped and remembered per
        algorithm, so switching away and back keeps your settings."""
        applied = self.controller.set_params(values or {})
        self.algorithm_params[self.algorithm_name] = applied
        return applied

    def set_drive(self, x: float = 0.0, y: float = 0.0) -> dict:
        """Direction input for manual mode. Ignored by autonomous algorithms."""
        driver = getattr(self.controller, "set_drive", None)
        if driver is None:
            return {}
        return driver(x, y)

    def set_floor_covering(self, name: str) -> None:
        """Set the house default, used by every room without its own."""
        if name not in COVERINGS:
            raise ValueError(f"unknown floor covering {name!r}")
        self.covering = COVERINGS[name]
        self._apply_surface()

    def set_room_covering(self, room_name: str, covering: str | None) -> None:
        """Give one room its own covering, or None to inherit the default."""
        room = self.world.house.find(room_name)
        if room is None:
            raise ValueError(f"no room named {room_name!r}")
        if covering is not None and covering not in COVERINGS:
            raise ValueError(f"unknown floor covering {covering!r}")
        room.covering = covering
        self._apply_surface()

    def set_physics(self, values: dict) -> dict:
        """Tune the chassis: acceleration, turn rate, bumper damping."""
        limits = self.vacuum.limits
        if values.get("maxAccel") is not None:
            limits.max_accel = clamp(values["maxAccel"], ACCEL_BOUNDS)
            limits.max_decel = min(4.0, limits.max_accel * 2)
        if values.get("maxTurnRate") is not None:
            limits.max_turn_rate = clamp(values["maxTurnRate"], TURN_BOUNDS)
        if values.get("bumperDamping") is not None:
            limits.bumper_damping = max(0.0, min(1.0, float(values["bumperDamping"])))
        if values.get("wheelBase") is not None:
            limits.wheel_base = max(0.08, min(0.6, float(values["wheelBase"])))
        return limits.to_dict()

    def set_efficiency(self, value: float, covering: str | None = None) -> None:
        """Set cleaning efficiency for one covering type (1.7.8)."""
        name = covering or self.covering.name
        if name not in COVERINGS:
            raise ValueError(f"unknown floor covering {name!r}")
        self.efficiencies[name] = clamp_efficiency(value)

    def place_obstruction(self, x, y, width, height, kind) -> Obstruction:
        if kind not in KINDS:
            raise ValueError(f"unknown obstruction kind {kind!r}")
        obstruction = Obstruction(float(x), float(y), float(width), float(height), kind)
        self.world.add_obstruction(obstruction)
        self._settle_vacuum()
        self.refresh_warnings()
        return obstruction

    def update_obstruction(self, obstruction_id: int, **changes) -> Obstruction:
        obstruction = self.world.find_obstruction(int(obstruction_id))
        if obstruction is None:
            raise ValueError(f"no obstruction with id {obstruction_id}")
        for field in ("x", "y", "width", "height"):
            if changes.get(field) is not None:
                setattr(obstruction, field, float(changes[field]))
        if changes.get("kind") is not None:
            if changes["kind"] not in KINDS:
                raise ValueError(f"unknown obstruction kind {changes['kind']!r}")
            obstruction.kind = changes["kind"]
        obstruction.width = max(0.1, obstruction.width)
        obstruction.height = max(0.1, obstruction.height)
        self.world.rebuild()
        self._settle_vacuum()
        self.refresh_warnings()
        return obstruction

    def remove_obstruction(self, obstruction_id: int) -> bool:
        removed = self.world.remove_obstruction(int(obstruction_id))
        if removed:
            self.refresh_warnings()
        return removed

    # -- room editing ---------------------------------------------------------
    def add_room(self, name, x, y, width, height, covering=None) -> Room:
        if covering is not None and covering not in COVERINGS:
            raise ValueError(f"unknown floor covering {covering!r}")
        room = Room(
            str(name or "room"),
            float(x),
            float(y),
            max(ROOM_MIN_SIDE, float(width)),
            max(ROOM_MIN_SIDE, float(height)),
            covering,
        )
        self.world.house.add_room(room)
        self._layout_changed()
        return room

    def update_room(self, name: str, **changes) -> Room:
        room = self.world.house.find(name)
        if room is None:
            raise ValueError(f"no room named {name!r}")
        for field in ("x", "y", "width", "height"):
            if changes.get(field) is not None:
                setattr(room, field, float(changes[field]))
        room.width = max(ROOM_MIN_SIDE, room.width)
        room.height = max(ROOM_MIN_SIDE, room.height)
        if "covering" in changes:
            self.set_room_covering(room.name, changes["covering"])
        new_name = changes.get("new_name")
        if new_name and new_name != room.name:
            room.name = self.world.house.unique_name(str(new_name))
        self._layout_changed()
        return room

    def remove_room(self, name: str) -> bool:
        if not self.world.house.remove_room(name):
            return False
        self._layout_changed()
        return True

    def _layout_changed(self) -> None:
        """Rooms moved: re-grid the house and put the run back to the start.

        The coverage grid is dimensioned from the house bounds, so a room edit
        invalidates it - the run resets rather than reporting coverage against
        a floor plan that no longer exists.
        """
        self.world.house_changed()
        self.vacuum.x, self.vacuum.y = self._spawn_point()
        self.vacuum.heading = 0.0
        self.vacuum.distance_travelled = 0.0
        self.controller.reset(self.vacuum)
        self._reset_run_state()
        self.refresh_warnings()

    def _settle_vacuum(self) -> None:
        if not self.world.is_free(self.vacuum.x, self.vacuum.y):
            self.vacuum.x, self.vacuum.y = self._spawn_point()

    # -- diagnostics ----------------------------------------------------------
    def refresh_warnings(self) -> list[Warning]:
        self.warnings = check_connectivity(
            self.world, self.vacuum.x, self.vacuum.y, self.vacuum.clean_radius
        )
        return self.warnings

    def _report(self, reason: str) -> dict:
        """Final coverage figures for a finished run (1.7.10)."""
        stats = self.world.grid.stats()
        return {
            "reason": reason,
            "simTime": round(self.sim_time, 2),
            "distanceTravelled": round(self.vacuum.distance_travelled, 2),
            "batteryUsed": round(100.0 - self.vacuum.battery.level, 2),
            "algorithm": self.algorithm_name,
            "floorCovering": self.covering.name,
            "efficiency": self.efficiency,
            **stats,
        }

    # -- serialisation ---------------------------------------------------------
    def get_state(self) -> dict:
        under = self.covering_at(self.vacuum.x, self.vacuum.y)
        return {
            "tick": self.tick,
            "simTime": round(self.sim_time, 3),
            "running": self.running,
            "status": self.status,
            "notice": self.notice,
            "algorithm": self.algorithm_name,
            "algorithmStatus": self.controller.status(),
            "algorithmParams": dict(self.controller.params),
            "vacuum": self.vacuum.to_dict(),
            "battery": self.vacuum.battery.to_dict(),
            "pendingSpeed": self.pending_speed,
            "stalledTicks": self.stalled_ticks,
            "floor": {
                "covering": self.covering.name,
                "label": self.covering.label,
                "color": self.covering.color,
                "efficiency": round(self.efficiency, 3),
                "efficiencies": {k: round(v, 3) for k, v in self.efficiencies.items()},
                "under": under.name,
                "underLabel": under.label,
                "traction": under.traction,
            },
            "coverage": self.world.grid.stats(),
            "warnings": [w.to_dict() for w in self.warnings],
            "finalReport": self.final_report,
        }

    def describe(self) -> dict:
        """Static setup plus a full coverage snapshot; sent on connect and on resync."""
        return {
            "dt": self.dt,
            "algorithms": describe_algorithms(),
            "coverings": coverings_dict(),
            "obstructionKinds": list(KINDS),
            "limits": {
                "speedMs": list(SPEED_BOUNDS_MS),
                "capacityMinutes": list(CAPACITY_BOUNDS_MIN),
                "efficiency": list(EFFICIENCY_BOUNDS),
                "maxAccel": list(ACCEL_BOUNDS),
                "maxTurnRate": list(TURN_BOUNDS),
            },
            **self.world.to_dict(),
            "coverageCells": self.world.grid.snapshot(),
        }
