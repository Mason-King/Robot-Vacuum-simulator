"""Obstructions placed inside the house.

Two kinds (1.7.1-1.7.4):
  blocking    - a wall unit, a kitchen island: the vacuum cannot pass and the
                floor underneath can never be cleaned (1.7.5/1.7.6).
  pass_under  - a couch or bed on legs: the vacuum drives underneath and the
                floor there still counts toward coverage.
"""

import itertools
from dataclasses import dataclass, field

BLOCKING = "blocking"
PASS_UNDER = "pass_under"
KINDS = (BLOCKING, PASS_UNDER)

_ids = itertools.count(1)


@dataclass
class Obstruction:
    x: float
    y: float
    width: float
    height: float
    kind: str = BLOCKING
    label: str = ""
    id: int = field(default_factory=lambda: next(_ids))

    def __post_init__(self):
        if self.kind not in KINDS:
            raise ValueError(f"unknown obstruction kind {self.kind!r}")
        if self.width <= 0 or self.height <= 0:
            raise ValueError("obstructions need a positive size")

    @property
    def x2(self) -> float:
        return self.x + self.width

    @property
    def y2(self) -> float:
        return self.y + self.height

    @property
    def area(self) -> float:
        return self.width * self.height

    @property
    def blocks_movement(self) -> bool:
        return self.kind == BLOCKING

    def contains(self, x: float, y: float) -> bool:
        return self.x <= x <= self.x2 and self.y <= y <= self.y2

    def intersects_circle(self, cx: float, cy: float, radius: float) -> bool:
        """True when a disc of `radius` at (cx, cy) touches this rectangle."""
        nearest_x = min(max(cx, self.x), self.x2)
        nearest_y = min(max(cy, self.y), self.y2)
        dx, dy = cx - nearest_x, cy - nearest_y
        return dx * dx + dy * dy < radius * radius

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "x": self.x,
            "y": self.y,
            "width": self.width,
            "height": self.height,
            "kind": self.kind,
            "label": self.label,
        }


def default_obstructions() -> list[Obstruction]:
    """A couple of pieces of furniture so both kinds are visible on boot."""
    return [
        Obstruction(1.0, 3.2, 1.9, 0.9, PASS_UNDER, "couch"),
        Obstruction(9.4, 0.6, 1.4, 1.0, BLOCKING, "kitchen island"),
        Obstruction(9.0, 4.6, 1.6, 2.0, PASS_UNDER, "bed"),
        Obstruction(0.6, 6.0, 1.2, 0.7, BLOCKING, "desk"),
    ]
