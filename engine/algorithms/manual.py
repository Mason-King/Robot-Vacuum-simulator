"""Manual drive: the user steers with WASD instead of an algorithm.

The keys name a direction on the floor plan, not a set of controls: W drives up
the plan, S down, A left, D right, and two keys together drive the diagonal
between them. The vacuum still has to turn to face where it is going - a
differential drive cannot slide sideways - so it swings onto the new heading
and eases off the throttle while it does.

It is a controller like any other, so the vacuum still accelerates, slips on
carpet and bumps into walls exactly as it does under an algorithm.
"""

import math

from ..physics import DriveCommand, wrap_angle
from .base import MovementAlgorithm


class Manual(MovementAlgorithm):
    name = "manual"
    label = "Manual (WASD)"
    self_driving = False
    PARAMS = {
        "turn_rate": {
            "label": "Turn rate", "unit": "rad/s",
            "min": 0.5, "max": 6.0, "step": 0.1, "default": 3.0,
        },
        "snap": {
            "label": "Move-off angle", "unit": "deg",
            "min": 15, "max": 180, "step": 5, "default": 100,
        },
    }

    def __init__(self, world, seed=None, params=None):
        super().__init__(world, seed, params)
        self.reset(None)

    def reset(self, vacuum) -> None:
        super().reset(vacuum)
        self.dx = 0.0
        self.dy = 0.0

    def set_drive(self, x: float = 0.0, y: float = 0.0) -> dict:
        """Held-input state from the client, as a direction on the floor plan.

        x is +1 for right / -1 for left, y is +1 for down / -1 for up, matching
        the plan's coordinates.
        """
        self.dx = max(-1.0, min(1.0, float(x)))
        self.dy = max(-1.0, min(1.0, float(y)))
        return {"x": self.dx, "y": self.dy}

    @property
    def heading(self) -> float | None:
        """The direction the keys are asking for, or None if none are held."""
        if self.dx == 0.0 and self.dy == 0.0:
            return None
        return math.atan2(self.dy, self.dx)

    def update(self, vacuum, dt: float) -> DriveCommand:
        desired = self.heading
        if desired is None:
            self.command = DriveCommand.stopped()
            return self.command

        # Turning through more than the move-off angle means the vacuum is
        # facing badly wrong: pivot first rather than driving a long arc back.
        error = abs(wrap_angle(desired - vacuum.heading))
        rate = self.params["turn_rate"]
        if error > math.radians(self.params["snap"]):
            turn = math.copysign(
                min(rate, vacuum.limits.max_turn_rate),
                wrap_angle(desired - vacuum.heading),
            )
            self.command = DriveCommand(0.0, turn)
        else:
            self.command = self.steer(vacuum, desired, turn_limit=rate)
        return self.command

    def status(self) -> dict:
        heading = self.heading
        return {
            "algorithm": self.name,
            "done": False,
            "input": [self.dx, self.dy],
            "headingDeg": None if heading is None else round(math.degrees(heading) % 360, 1),
            "params": dict(self.params),
        }
