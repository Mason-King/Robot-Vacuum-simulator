"""Boustrophedon sweep: back-and-forth lanes, room by room.

The planner is unchanged - lanes clipped to the reachable part of each room,
rooms visited through doorways - but it now hands the chassis a heading and a
speed rather than a position, so the vacuum arcs into each lane instead of
pivoting on a dime.
"""

import math
from collections import deque

from ..house import Room
from ..physics import DriveCommand
from .base import MovementAlgorithm

WALL_CLEARANCE = 0.05
ARRIVE_RADIUS = 0.09  # a body with momentum cannot land exactly on a point
STUCK_TICKS = 30  # give up on a target after ~1s of no progress toward it
PROGRESS_EPS = 0.005  # metres of closing distance that counts as progress
RECOVER_TICKS = 10


class Snaking(MovementAlgorithm):
    name = "snaking"
    label = "Snaking (lane sweep)"
    PARAMS = {
        "lane_spacing": {
            "label": "Lane spacing", "unit": "m",
            "min": 0.10, "max": 1.00, "step": 0.02, "default": 0.30,
        },
    }

    def __init__(self, world, seed=None, params=None):
        self.targets = deque()
        self.covered = []
        self.current_room = None
        super().__init__(world, seed, params)
        self.reset(None)

    @property
    def lane_spacing(self) -> float:
        return self.params["lane_spacing"]

    def on_params_changed(self) -> None:
        """Re-sweep the room in progress at the new spacing."""
        if getattr(self, "current_room", None) is None:
            return
        self.targets.clear()
        if self.current_room.name in self.covered:
            self.covered.remove(self.current_room.name)
        self.current_room = None

    def reset(self, vacuum) -> None:
        super().reset(vacuum)
        self.targets = deque()
        self.covered = []
        self.current_room = None
        self.stuck = 0
        self.skipped = 0
        self.recovering = 0
        self.best_gap = float("inf")

    # -- planning ----------------------------------------------------------
    def _is_reachable(self, point, reached) -> bool:
        """True if the vacuum can actually get to (or right next to) this spot."""
        grid = self.world.grid
        index = grid.index(*point)
        if index is None:
            return False
        col, row = index % grid.cols, index // grid.cols
        for dcol in (0, -1, 1):
            for drow in (0, -1, 1):
                ncol, nrow = col + dcol, row + drow
                if 0 <= ncol < grid.cols and 0 <= nrow < grid.rows:
                    if reached[nrow * grid.cols + ncol]:
                        return True
        return False

    def _lane_targets(self, room: Room, vacuum, reached) -> list[tuple[float, float]]:
        """Lane ends for one room, each lane clipped to the part of it the
        vacuum can actually reach."""
        inset = vacuum.radius + WALL_CLEARANCE
        x_lo, x_hi = room.x + inset, room.x2 - inset
        y_lo, y_hi = room.y + inset, room.y2 - inset
        if x_hi <= x_lo or y_hi <= y_lo:
            return []

        probe = self.world.grid.cell_size
        targets, y, rightward = [], y_lo, True
        while True:
            span = []
            x = x_lo
            while x < x_hi:
                if self._is_reachable((x, y), reached):
                    span.append(x)
                x += probe
            # Sample the exact far end too - stepping by cell size alone leaves
            # every lane up to a cell short, which adds up over a whole house.
            if self._is_reachable((x_hi, y), reached):
                span.append(x_hi)
            if span:
                lo, hi = min(span), max(span)
                first, second = (lo, hi) if rightward else (hi, lo)
                targets.append((first, y))
                targets.append((second, y))
            if y >= y_hi - 1e-4:
                break
            y = min(y + self.lane_spacing, y_hi)
            rightward = not rightward
        return targets

    def _plan(self, vacuum) -> None:
        """Queue the next room worth sweeping.

        Lane ends the vacuum cannot physically get to are dropped up front -
        one flood fill here beats discovering each one by driving into it.
        """
        if self.current_room is not None and self.current_room.name not in self.covered:
            self.covered.append(self.current_room.name)

        reached = self.world.grid.reachable_from(vacuum.x, vacuum.y)
        while True:
            room = next(
                (r for r in self.world.house.rooms if r.name not in self.covered), None
            )
            if room is None:
                self.done = True
                self.current_room = None
                return

            lanes = self._lane_targets(room, vacuum, reached)
            if not lanes:
                # Walled off from here - the connectivity warning already says so.
                self.covered.append(room.name)
                continue

            here = (
                self.world.house.room_at(vacuum.x, vacuum.y)
                or self.world.house.rooms[0]
            )
            self.targets.extend(self.world.house.route(here, room))
            self.targets.extend(lanes)
            self.current_room = room
            return

    def _next_target(self, vacuum) -> None:
        if self.targets:
            self.targets.popleft()
        self.stuck = 0
        self.best_gap = float("inf")
        if not self.targets:
            self._plan(vacuum)

    # -- per-tick ----------------------------------------------------------
    def update(self, vacuum, dt: float) -> DriveCommand:
        if self.done:
            self.command = DriveCommand.stopped()
            return self.command

        if self.recovering > 0:
            self.recovering -= 1
            normal = vacuum.contact.normal if vacuum.contact else None
            self.command = self.back_off(vacuum, normal)
            return self.command

        if not self.targets:
            self._plan(vacuum)
            if self.done:
                self.command = DriveCommand.stopped()
                return self.command

        target = self.targets[0]
        gap = math.hypot(target[0] - vacuum.x, target[1] - vacuum.y)
        if gap <= ARRIVE_RADIUS:
            self._next_target(vacuum)
            if self.done or not self.targets:
                self.command = DriveCommand.stopped()
                return self.command
            target = self.targets[0]
            gap = math.hypot(target[0] - vacuum.x, target[1] - vacuum.y)

        # Furniture parked on a lane end means the target never gets closer.
        # Write it off after a second and let the next lane resume the sweep.
        if gap < self.best_gap - PROGRESS_EPS:
            self.best_gap = gap
            self.stuck = 0
        else:
            self.stuck += 1
            if self.stuck >= STUCK_TICKS:
                self.skipped += 1
                if vacuum.contact is not None:
                    self.recovering = RECOVER_TICKS
                self._next_target(vacuum)
                if self.done or not self.targets:
                    self.command = DriveCommand.stopped()
                    return self.command
                target = self.targets[0]

        self.command = self.steer_to(vacuum, *target)
        return self.command

    def status(self) -> dict:
        return {
            "algorithm": self.name,
            "done": self.done,
            "currentRoom": self.current_room.name if self.current_room else None,
            "roomsCovered": list(self.covered),
            "roomsTotal": len(self.world.house.rooms),
            "skippedTargets": self.skipped,
            "params": dict(self.params),
        }
