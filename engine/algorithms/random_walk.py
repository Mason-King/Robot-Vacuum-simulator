"""Random bounce: drive straight, and reflect off whatever you hit."""

import math

from .base import MovementAlgorithm
from ..physics import DriveCommand, wrap_angle

PROBE_TRIES = 24


class RandomWalk(MovementAlgorithm):
    name = "random"
    label = "Random bounce"
    PARAMS = {
        "min_leg": {
            "label": "Shortest leg", "unit": "s",
            "min": 0.5, "max": 10.0, "step": 0.5, "default": 3.0,
        },
        "max_leg": {
            "label": "Longest leg", "unit": "s",
            "min": 1.0, "max": 20.0, "step": 0.5, "default": 9.0,
        },
        "scatter": {
            "label": "Bounce scatter", "unit": "rad",
            "min": 0.0, "max": 1.5, "step": 0.05, "default": 0.5,
        },
    }

    def __init__(self, world, seed=None, params=None):
        super().__init__(world, seed, params)
        self.reset(None)

    def reset(self, vacuum) -> None:
        super().reset(vacuum)
        self.heading = vacuum.heading if vacuum else self.rng.uniform(0, 2 * math.pi)
        self.leg_remaining = self._leg()
        self.bounces = 0

    def _leg(self) -> float:
        low = self.params["min_leg"]
        return self.rng.uniform(low, max(low, self.params["max_leg"]))

    def _open(self, vacuum, heading: float, distance: float) -> bool:
        return self.world.is_free(
            vacuum.x + math.cos(heading) * distance,
            vacuum.y + math.sin(heading) * distance,
            vacuum.radius,
        )

    def _reflect(self, vacuum) -> None:
        """Mirror the heading about the contact normal, then scatter it."""
        normal = vacuum.contact.normal
        mirrored = wrap_angle(2 * normal - vacuum.heading + math.pi)
        scatter = self.params["scatter"]
        for _ in range(PROBE_TRIES):
            candidate = wrap_angle(mirrored + self.rng.uniform(-scatter, scatter))
            if self._open(vacuum, candidate, vacuum.radius + 0.2):
                self.heading = candidate
                break
        else:
            self.heading = normal
        self.leg_remaining = self._leg()
        self.bounces += 1

    def update(self, vacuum, dt: float) -> DriveCommand:
        if vacuum.contact is not None:
            self._reflect(vacuum)
        else:
            self.leg_remaining -= dt
            if self.leg_remaining <= 0:
                for _ in range(PROBE_TRIES):
                    candidate = self.rng.uniform(-math.pi, math.pi)
                    if self._open(vacuum, candidate, vacuum.radius + 0.3):
                        self.heading = candidate
                        break
                self.leg_remaining = self._leg()

        self.command = self.steer(vacuum, self.heading)
        return self.command

    def status(self) -> dict:
        return {
            "algorithm": self.name,
            "done": False,
            "headingDeg": round(math.degrees(self.heading) % 360, 1),
            "bounces": self.bounces,
            "params": dict(self.params),
        }
