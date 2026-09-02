"""Common interface for movement algorithms (1.6.1 / Tier 2's 2.3).

An algorithm is a *controller*: it looks at the world and publishes a
`DriveCommand` for the tick. It never sets a position or a heading - the
chassis in `engine.physics` decides how much of that command the motors,
the floor and the walls actually allow.

Each algorithm declares its tunable knobs in PARAMS, which is what the UI
builds its sliders from - adding a parameter needs no client changes.
"""

import math
import random

from ..physics import DriveCommand, wrap_angle

#: How hard to correct a heading error, in rad/s per radian of error.
HEADING_GAIN = 4.0


class MovementAlgorithm:
    name = "base"
    label = "Base"
    #: False for driver-controlled modes, where standing still is intentional
    #: and the simulation's anti-stall recovery must not interfere.
    self_driving = True
    #: parameter name -> {label, min, max, step, default, unit}
    PARAMS: dict[str, dict] = {}

    def __init__(self, world, seed: int | None = None, params: dict | None = None):
        self.world = world
        self.rng = random.Random(seed)
        self.done = False
        self.command = DriveCommand.stopped()
        self.params = {key: spec["default"] for key, spec in self.PARAMS.items()}
        if params:
            self.params.update({k: v for k, v in params.items() if k in self.PARAMS})
        self.on_params_changed()

    # -- steering helpers ---------------------------------------------------
    def steer(
        self,
        vacuum,
        desired_heading: float,
        speed: float | None = None,
        turn_limit: float | None = None,
    ):
        """Proportional heading control.

        The throttle falls off with the cosine of the heading error, so the
        vacuum slows into a turn and pivots on the spot when the target is
        behind it - the same thing a real robot does, and the reason it no
        longer scrubs sideways into walls.
        """
        error = wrap_angle(desired_heading - vacuum.heading)
        limit = vacuum.limits.max_turn_rate
        if turn_limit is not None:
            limit = min(limit, turn_limit)
        turn = max(-limit, min(limit, HEADING_GAIN * error))
        cruise = vacuum.speed if speed is None else speed
        return DriveCommand(cruise * max(0.0, math.cos(error)), turn)

    def steer_to(self, vacuum, tx: float, ty: float, speed: float | None = None):
        return self.steer(vacuum, math.atan2(ty - vacuum.y, tx - vacuum.x), speed)

    def back_off(self, vacuum, away_from: float | None = None):
        """Reverse and swing away - the response to being wedged."""
        turn = vacuum.limits.max_turn_rate * 0.8
        if away_from is not None:
            turn *= 1.0 if wrap_angle(away_from - vacuum.heading) > 0 else -1.0
        else:
            turn *= self.rng.choice((1.0, -1.0))
        return DriveCommand(-vacuum.speed * 0.45, turn)

    # -- parameters ---------------------------------------------------------
    @classmethod
    def describe_params(cls) -> list[dict]:
        return [{"name": key, **spec} for key, spec in cls.PARAMS.items()]

    def set_params(self, values: dict) -> dict:
        """Apply and clamp a partial set of parameters. Safe to call mid-run."""
        for key, value in values.items():
            spec = self.PARAMS.get(key)
            if spec is None:
                continue
            self.params[key] = max(spec["min"], min(spec["max"], float(value)))
        self.on_params_changed()
        return dict(self.params)

    def on_params_changed(self) -> None:
        """Hook for algorithms that must re-plan when a knob moves."""

    # -- lifecycle ----------------------------------------------------------
    def reset(self, vacuum) -> None:
        self.done = False
        self.command = DriveCommand.stopped()

    def update(self, vacuum, dt: float) -> DriveCommand:
        """Publish this tick's drive command."""
        raise NotImplementedError

    def status(self) -> dict:
        return {"algorithm": self.name, "done": self.done, "params": dict(self.params)}
