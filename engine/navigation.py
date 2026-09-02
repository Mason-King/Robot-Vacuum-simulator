"""The world the vacuum moves through: house + obstructions + coverage grid.

Everything geometric lives here so the movement algorithms stay small: they
choose a heading or a target and ask the world to move the vacuum, which
handles walls, blocking obstructions and sub-stepping.
"""

import math

from .coverage import DEFAULT_CELL_SIZE, CoverageGrid
from .house import House
from .obstruction import Obstruction

SUBSTEP = 0.02  # metres per collision sample
#: headings tried when the vacuum is wedged, fanning out from where it faces
ESCAPE_FAN = [0.0] + [
    s * a for a in (0.35, 0.7, 1.05, 1.4, 1.75, 2.1, 2.45, 2.8, math.pi) for s in (1, -1)
]
_PERIMETER = [
    (math.cos(a * math.pi / 4), math.sin(a * math.pi / 4)) for a in range(8)
]


class World:
    def __init__(
        self,
        house: House,
        obstructions: list[Obstruction] | None = None,
        cell_size: float = DEFAULT_CELL_SIZE,
        vacuum_radius: float = 0.17,
    ):
        self.house = house
        self.obstructions = list(obstructions or [])
        self.vacuum_radius = vacuum_radius
        self.grid = CoverageGrid(house, cell_size)
        self.rebuild()

    # -- world queries -----------------------------------------------------
    @property
    def blocking(self) -> list[Obstruction]:
        return [o for o in self.obstructions if o.blocks_movement]

    def in_house(self, x: float, y: float) -> bool:
        return self.house.is_walkable(x, y)

    def is_cleanable(self, x: float, y: float) -> bool:
        """Floor that can ever be cleaned: inside the house, not under a blocker."""
        if not self.in_house(x, y):
            return False
        return not any(o.contains(x, y) for o in self.blocking)

    def point_is_open(self, x: float, y: float) -> bool:
        """Is this single point on open floor? Used to probe contact normals."""
        if not self.in_house(x, y):
            return False
        return not any(o.contains(x, y) for o in self.blocking)

    def is_free(self, x: float, y: float, radius: float | None = None) -> bool:
        """True when a vacuum of `radius` centred here fits on open floor.

        The disc is sampled at its centre and eight points on its rim, each
        tested against the *union* of rooms - testing against one room at a
        time would wrongly seal every doorway, where the disc necessarily
        straddles two rectangles.
        """
        radius = self.vacuum_radius if radius is None else radius
        if not self.in_house(x, y):
            return False
        for dx, dy in _PERIMETER:
            if not self.in_house(x + dx * radius, y + dy * radius):
                return False
        return not any(o.intersects_circle(x, y, radius) for o in self.blocking)

    def probe(self, x: float, y: float, heading: float, distance: float) -> bool:
        """Is the floor `distance` ahead along `heading` free?"""
        return self.is_free(x + math.cos(heading) * distance,
                            y + math.sin(heading) * distance)

    # -- movement ----------------------------------------------------------
    def advance(
        self, vacuum, heading: float, distance: float, slide: bool = False
    ) -> tuple[float, bool]:
        """Move along `heading`, stopping at the first obstruction.

        With `slide`, a step that is blocked diagonally is retried on each axis
        alone, so the vacuum grazes along a wall instead of pinning itself
        against it - a target-following algorithm otherwise deadlocks whenever
        its target sits on the far side of a wall it is already touching.

        Returns (distance actually moved, whether something got in the way).
        """
        vacuum.heading = heading
        dx, dy = math.cos(heading), math.sin(heading)
        moved = 0.0
        remaining = distance
        blocked = False

        while remaining > 1e-9:
            step = min(SUBSTEP, remaining)
            nx, ny = vacuum.x + dx * step, vacuum.y + dy * step
            if not self.is_free(nx, ny):
                blocked = True
                if not slide:
                    return moved, True
                if self.is_free(vacuum.x + dx * step, vacuum.y):
                    nx, ny = vacuum.x + dx * step, vacuum.y
                elif self.is_free(vacuum.x, vacuum.y + dy * step):
                    nx, ny = vacuum.x, vacuum.y + dy * step
                else:
                    return moved, True
            travelled = math.hypot(nx - vacuum.x, ny - vacuum.y)
            vacuum.x, vacuum.y = nx, ny
            vacuum.distance_travelled += travelled
            moved += travelled
            remaining -= step
        return moved, blocked

    def step_toward(
        self, vacuum, tx: float, ty: float, distance: float, slide: bool = False
    ):
        """Move up to `distance` toward a target. Returns (moved, blocked, reached)."""
        dx, dy = tx - vacuum.x, ty - vacuum.y
        gap = math.hypot(dx, dy)
        if gap < 1e-6:
            return 0.0, False, True
        heading = math.atan2(dy, dx)
        moved, blocked = self.advance(vacuum, heading, min(distance, gap), slide)
        reached = math.hypot(tx - vacuum.x, ty - vacuum.y) < 1e-6
        return moved, blocked, reached

    def escape(self, vacuum, distance: float, preferred: float | None = None) -> float:
        """Get moving again from a wedged position.

        Sweeps outward from `preferred` (or the current heading) and takes the
        first direction with room in it, so a vacuum that drives into a corner
        slides out along the nearest opening instead of sitting there. Returns
        the distance actually moved - 0.0 only if it is sealed in on all sides.
        """
        base = vacuum.heading if preferred is None else preferred
        for offset in ESCAPE_FAN:
            moved, _ = self.advance(vacuum, base + offset, distance, slide=True)
            if moved > 1e-9:
                return moved
        return 0.0

    # -- maintenance --------------------------------------------------------
    def rebuild(self) -> None:
        """Recompute the coverage grid after the obstruction layout changes."""
        self.grid.rebuild(
            in_house=self.in_house,
            cleanable=self.is_cleanable,
            passable=lambda x, y: self.is_free(x, y),
        )

    def house_changed(self) -> None:
        """Rebuild after a room edit.

        Editing rooms can move the outer bounds, so the grid is re-created
        rather than re-masked - cleaning progress does not survive a layout
        change, and clients resync from the fresh snapshot.
        """
        self.grid = CoverageGrid(self.house, self.grid.cell_size)
        self.rebuild()

    def add_obstruction(self, obstruction: Obstruction) -> Obstruction:
        self.obstructions.append(obstruction)
        self.rebuild()
        return obstruction

    def find_obstruction(self, obstruction_id: int) -> Obstruction | None:
        return next((o for o in self.obstructions if o.id == obstruction_id), None)

    def remove_obstruction(self, obstruction_id: int) -> bool:
        obstruction = self.find_obstruction(obstruction_id)
        if obstruction is None:
            return False
        self.obstructions.remove(obstruction)
        self.rebuild()
        return True

    def clear_obstructions(self) -> None:
        self.obstructions.clear()
        self.rebuild()

    def to_dict(self) -> dict:
        return {
            "house": self.house.to_dict(),
            "obstructions": [o.to_dict() for o in self.obstructions],
            "grid": self.grid.to_dict(),
        }
