"""The vacuum entity and its battery."""

import math
from dataclasses import dataclass, field

M_PER_FT = 0.3048

SPEED_BOUNDS_MS = (0.05, 2.0)
ACCEL_BOUNDS = (0.1, 4.0)
TURN_BOUNDS = (0.3, 8.0)
CAPACITY_BOUNDS_MIN = (1.0, 480.0)
DEFAULT_CAPACITY_MIN = 90.0


def clamp(value: float, bounds: tuple[float, float]) -> float:
    return max(bounds[0], min(bounds[1], float(value)))


def to_metres_per_second(value: float, unit: str = "m/s") -> float:
    """Accept a speed in either unit and return m/s, clamped to bounds (1.3.5)."""
    factor = M_PER_FT if unit in ("ft/s", "fts", "ft") else 1.0
    return clamp(float(value) * factor, SPEED_BOUNDS_MS)


@dataclass
class DriveLimits:
    """What the motors and chassis can physically do."""

    max_accel: float = 0.7          # m/s^2 getting up to speed
    max_decel: float = 1.4          # m/s^2 braking - always stops faster than it starts
    max_turn_rate: float = 2.6      # rad/s
    max_angular_accel: float = 9.0  # rad/s^2
    wheel_base: float = 0.23        # m between the drive wheels
    bumper_damping: float = 0.65    # share of head-on speed the bumper absorbs

    def to_dict(self) -> dict:
        return {
            "maxAccel": self.max_accel,
            "maxDecel": self.max_decel,
            "maxTurnRate": self.max_turn_rate,
            "maxAngularAccel": self.max_angular_accel,
            "wheelBase": self.wheel_base,
            "bumperDamping": self.bumper_damping,
        }


@dataclass
class Surface:
    """How the floor under the wheels behaves."""

    traction: float = 1.0  # share of commanded motion that reaches the floor
    slip: float = 0.0      # random wheel slip, as a fraction of full turn rate


@dataclass
class Battery:
    """Charge measured in run-minutes; drains with simulated time (1.3.2)."""

    capacity_minutes: float = DEFAULT_CAPACITY_MIN
    remaining_seconds: float = field(default=None)  # type: ignore[assignment]

    def __post_init__(self):
        self.capacity_minutes = clamp(self.capacity_minutes, CAPACITY_BOUNDS_MIN)
        if self.remaining_seconds is None:
            self.remaining_seconds = self.capacity_minutes * 60.0

    @property
    def level(self) -> float:
        """Percent remaining."""
        capacity = self.capacity_minutes * 60.0
        return 100.0 * self.remaining_seconds / capacity if capacity else 0.0

    @property
    def empty(self) -> bool:
        return self.remaining_seconds <= 0.0

    def drain(self, dt: float, load: float = 1.0) -> None:
        """Discharge for `dt` seconds at `load` times the nominal draw.

        Capacity is quoted as run-minutes at nominal load, so a vacuum driving
        flat out empties the pack in exactly its rated time while one sitting
        still on standby lasts far longer.
        """
        self.remaining_seconds = max(0.0, self.remaining_seconds - dt * max(0.0, load))

    def set_capacity(self, minutes: float) -> None:
        """Resize the pack, keeping the same percentage of charge (1.3.4)."""
        fraction = self.level / 100.0
        self.capacity_minutes = clamp(minutes, CAPACITY_BOUNDS_MIN)
        self.remaining_seconds = self.capacity_minutes * 60.0 * fraction

    def recharge(self) -> None:
        self.remaining_seconds = self.capacity_minutes * 60.0

    def to_dict(self) -> dict:
        return {
            "level": round(self.level, 2),
            "capacityMinutes": round(self.capacity_minutes, 2),
            "remainingMinutes": round(self.remaining_seconds / 60.0, 2),
            "empty": self.empty,
        }


@dataclass
class Vacuum:
    x: float = 0.0
    y: float = 0.0
    heading: float = 0.0  # radians, 0 = +x
    speed: float = 0.35  # metres/second - held constant during a run (1.3.6)
    radius: float = 0.17  # chassis half-width, used for collision
    brush_margin: float = 0.06  # side brushes sweep a little wider than the body
    battery: Battery = field(default_factory=Battery)
    limits: DriveLimits = field(default_factory=DriveLimits)
    surface: Surface = field(default_factory=Surface)
    distance_travelled: float = 0.0

    # live motion state, owned by the chassis
    velocity: float = 0.0          # m/s along the heading
    angular_velocity: float = 0.0  # rad/s
    wheel_left: float = 0.0        # m/s at the contact patch
    wheel_right: float = 0.0
    contact: object = None         # engine.physics.Contact, on the tick it hits
    touching: bool = False         # still pressed against something
    bumps: int = 0

    @property
    def turn_radius(self) -> float | None:
        """Radius of the arc the vacuum is currently tracing, or None if it is
        turning on the spot / driving straight."""
        if abs(self.angular_velocity) < 1e-3 or abs(self.velocity) < 1e-3:
            return None
        return self.velocity / self.angular_velocity

    @property
    def motor_load(self) -> float:
        """Nominal-relative power draw: standby, plus drive, plus turning."""
        drive = abs(self.velocity) / max(self.speed, 1e-6)
        turn = abs(self.angular_velocity) / max(self.limits.max_turn_rate, 1e-6)
        return min(1.6, 0.3 + 0.7 * drive + 0.25 * turn)

    def halt(self) -> None:
        self.velocity = 0.0
        self.angular_velocity = 0.0
        self.wheel_left = self.wheel_right = 0.0
        self.contact = None
        self.touching = False

    @property
    def clean_radius(self) -> float:
        return self.radius + self.brush_margin

    @property
    def speed_fts(self) -> float:
        return self.speed / M_PER_FT

    def set_speed(self, value: float, unit: str = "m/s") -> float:
        self.speed = to_metres_per_second(value, unit)
        return self.speed

    def to_dict(self) -> dict:
        return {
            "x": round(self.x, 4),
            "y": round(self.y, 4),
            "heading": round(self.heading, 4),
            "headingDeg": round(math.degrees(self.heading) % 360.0, 1),
            "speedMs": round(self.speed, 3),
            "speedFts": round(self.speed_fts, 3),
            "radius": self.radius,
            "cleanRadius": round(self.clean_radius, 3),
            "distanceTravelled": round(self.distance_travelled, 2),
            "velocity": round(self.velocity, 3),
            "angularVelocity": round(self.angular_velocity, 3),
            "wheels": [round(self.wheel_left, 3), round(self.wheel_right, 3)],
            "turnRadius": None if self.turn_radius is None else round(self.turn_radius, 2),
            "motorLoad": round(self.motor_load, 3),
            "contact": self.contact.to_dict() if self.contact else None,
            "touching": self.touching,
            "bumps": self.bumps,
            "limits": self.limits.to_dict(),
        }
