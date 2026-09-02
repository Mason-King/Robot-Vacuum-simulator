"""Wall following: drive until something is hit, then hug it on the right."""

import math

from .base import MovementAlgorithm
from ..physics import DriveCommand, wrap_angle


class WallFollowing(MovementAlgorithm):
    name = "wall_following"
    label = "Wall following"
    PARAMS = {
        "standoff": {
            "label": "Wall standoff", "unit": "m",
            "min": 0.02, "max": 0.40, "step": 0.01, "default": 0.12,
        },
        "gain": {
            "label": "Steering gain", "unit": "",
            "min": 0.5, "max": 8.0, "step": 0.1, "default": 3.0,
        },
    }

    def __init__(self, world, seed=None, params=None):
        super().__init__(world, seed, params)
        self.reset(None)

    def reset(self, vacuum) -> None:
        super().reset(vacuum)
        self.hugging = False

    def _range(self, vacuum, heading: float, reach: float) -> float:
        """Distance to the first blocked sample along `heading`, up to `reach`."""
        step = 0.03
        travelled = step
        while travelled <= reach:
            x = vacuum.x + math.cos(heading) * travelled
            y = vacuum.y + math.sin(heading) * travelled
            if not self.world.point_is_open(x, y):
                return travelled
            travelled += step
        return reach

    def update(self, vacuum, dt: float) -> DriveCommand:
        standoff = self.params["standoff"]
        gain = self.params["gain"]
        reach = vacuum.radius + standoff * 3
        turn_limit = vacuum.limits.max_turn_rate

        front = self._range(vacuum, vacuum.heading, reach)
        right = self._range(vacuum, vacuum.heading + math.pi / 2, reach)

        # Bumped into something: pivot away from the surface we touched.
        if vacuum.contact is not None:
            self.hugging = True
            away = wrap_angle(vacuum.contact.normal - vacuum.heading)
            self.command = DriveCommand(
                vacuum.speed * 0.15, turn_limit * (-1.0 if away < 0 else 1.0)
            )
            return self.command

        if front < vacuum.radius + standoff:
            # Wall ahead: turn away from the right-hand wall, keep creeping.
            self.hugging = True
            self.command = DriveCommand(vacuum.speed * 0.2, -turn_limit)
            return self.command

        if not self.hugging:
            self.command = DriveCommand(vacuum.speed, 0.0)  # go find a wall
            return self.command

        # Hold the wall at `standoff` on the right: too far turns toward it,
        # too close turns away. `right` saturating at `reach` also curves us
        # back in when the wall disappears at a doorway.
        error = right - (vacuum.radius + standoff)
        self.command = DriveCommand(
            vacuum.speed, max(-turn_limit, min(turn_limit, gain * error))
        )
        return self.command

    def status(self) -> dict:
        return {
            "algorithm": self.name,
            "done": False,
            "hugging": self.hugging,
            "params": dict(self.params),
        }
