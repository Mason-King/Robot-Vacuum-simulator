"""Expanding spiral, restarted wherever the vacuum runs into something.

With a real chassis this is the honest way to spiral: hold a constant speed
and set the turn rate to v / r, then let r grow. The path that comes out is
an Archimedean spiral because that is what those two commands produce - no
waypoints involved.
"""

import math

from .base import MovementAlgorithm
from ..physics import DriveCommand

RECOVER_TICKS = 12


class Spiral(MovementAlgorithm):
    name = "spiral"
    label = "Expanding spiral"
    PARAMS = {
        "spacing": {
            "label": "Ring spacing", "unit": "m",
            "min": 0.10, "max": 1.00, "step": 0.02, "default": 0.30,
        },
        "max_radius": {
            "label": "Restart radius", "unit": "m",
            "min": 0.50, "max": 5.00, "step": 0.10, "default": 2.50,
        },
        "seek": {
            "label": "Relocate time", "unit": "s",
            "min": 0.0, "max": 8.0, "step": 0.25, "default": 6.0,
        },
    }

    def __init__(self, world, seed=None, params=None):
        super().__init__(world, seed, params)
        self.reset(None)

    def reset(self, vacuum) -> None:
        super().reset(vacuum)
        self.radius = self._min_radius(vacuum)
        self.direction = 1.0
        self.recovering = 0
        self.seeking = 0
        self.seek_heading = 0.0
        self.spirals = 0

    def _min_radius(self, vacuum) -> float:
        """Radius the first ring starts at.

        Never tighter than the vacuum is wide - a circle smaller than the body
        is a pirouette, not a spiral, and it needs more clearance to start than
        the floor usually has.
        """
        if vacuum is None:
            return 0.25
        return max(vacuum.radius * 1.6, vacuum.speed / vacuum.limits.max_turn_rate)

    def _restart(self, vacuum) -> None:
        """Back off, drive somewhere with room, then start the next spiral.

        Restarting on the spot just wedges the vacuum against the same wall,
        which is why a real one leaves before it winds up again.
        """
        self.radius = self._min_radius(vacuum)
        self.direction = self.rng.choice((1.0, -1.0))
        self.recovering = RECOVER_TICKS
        self.seeking = int(self.params["seek"] * 30)  # seconds -> ticks
        self.seek_heading = self._open_heading(vacuum)
        self.spirals += 1

    def _open_heading(self, vacuum) -> float:
        """The sampled direction with the most clear floor in front of it."""
        best, best_clear = vacuum.heading + math.pi, -1.0
        for i in range(16):
            heading = -math.pi + 2 * math.pi * i / 16
            clear = 0.0
            while clear < 3.0:
                probe = clear + 0.15
                if not self.world.is_free(
                    vacuum.x + math.cos(heading) * probe,
                    vacuum.y + math.sin(heading) * probe,
                    vacuum.radius,
                ):
                    break
                clear = probe
            if clear > best_clear:
                best, best_clear = heading, clear
        return best

    def update(self, vacuum, dt: float) -> DriveCommand:
        # Only a spiral in progress reacts to contact. Backing away from a wall
        # brushes it again, and treating that as a fresh hit restarts the
        # manoeuvre on top of itself - the vacuum never gets clear.
        if vacuum.contact is not None and self.recovering == 0 and self.seeking == 0:
            self._restart(vacuum)

        if self.recovering > 0:
            self.recovering -= 1
            normal = vacuum.contact.normal if vacuum.contact else None
            self.command = self.back_off(vacuum, normal)
            return self.command

        if self.seeking > 0:
            self.seeking -= 1
            self.command = self.steer(vacuum, self.seek_heading)
            return self.command

        # Grow the radius by `spacing` for every full turn: dr/dtheta = s/2pi.
        swept = abs(vacuum.angular_velocity) * dt
        self.radius += self.params["spacing"] * swept / (2 * math.pi)
        if self.radius > self.params["max_radius"]:
            self._restart(vacuum)
            return self.command

        turn = vacuum.speed / self.radius
        limit = vacuum.limits.max_turn_rate
        self.command = DriveCommand(
            vacuum.speed, self.direction * max(-limit, min(limit, turn))
        )
        return self.command

    def status(self) -> dict:
        return {
            "algorithm": self.name,
            "done": False,
            "spiralRadius": round(self.radius, 2),
            "spirals": self.spirals,
            "seeking": self.seeking > 0,
            "params": dict(self.params),
        }
