"""The vacuum's drive model.

Until now the vacuum was a point that teleported along a heading: it turned
instantly, reached full speed instantly, and stopped dead on contact. This
module replaces that with a differential-drive chassis:

  * two wheels, so heading changes come from a speed difference across the
    axle rather than from assignment, and the turn radius is v / omega;
  * motors with acceleration limits, so the body has momentum;
  * surface traction per floor covering, so carpet is slower and drifts;
  * a bumper that resolves contact against a surface normal - the vacuum
    slides along a wall it grazes and rebounds off one it hits square on.

Algorithms no longer move the vacuum. They publish a `DriveCommand` and this
integrates it, which is what makes them controllers rather than animators.
"""

import math
from dataclasses import dataclass

CONTACT_SAMPLES = 16  # rim directions probed when working out a contact normal
MIN_SUBSTEP = 0.004  # metres


def wrap_angle(angle: float) -> float:
    """Fold an angle into (-pi, pi]."""
    return (angle + math.pi) % (2 * math.pi) - math.pi


def approach(current: float, target: float, up: float, down: float) -> float:
    """Move `current` toward `target`, limited by separate rise/fall rates."""
    if target > current:
        return min(target, current + up)
    return max(target, current - down)


@dataclass
class DriveCommand:
    """What a controller asks of the chassis this tick."""

    speed: float = 0.0  # metres/second, negative reverses
    turn: float = 0.0  # radians/second, signed the same way as `heading`

    @classmethod
    def stopped(cls) -> "DriveCommand":
        return cls(0.0, 0.0)


@dataclass
class Contact:
    """Where the bumper touched, and how hard."""

    normal: float  # heading of the outward surface normal
    impact: float  # closing speed at the moment of contact, m/s

    def to_dict(self) -> dict:
        return {"normal": round(self.normal, 3), "impact": round(self.impact, 3)}


class Chassis:
    """Integrates a drive command into a pose, resolving contact as it goes."""

    def __init__(self, rng):
        self.rng = rng

    # -- contact geometry --------------------------------------------------
    def contact_normal(self, world, x: float, y: float, radius: float) -> float | None:
        """Outward normal at a contact, from which rim directions are blocked.

        Averaging the blocked directions and negating gives the way out, which
        works the same for a flat wall, an inside corner or a table leg.
        """
        blocked_x = blocked_y = 0.0
        blocked = 0
        for i in range(CONTACT_SAMPLES):
            angle = 2 * math.pi * i / CONTACT_SAMPLES
            probe_x = x + math.cos(angle) * radius
            probe_y = y + math.sin(angle) * radius
            if not world.point_is_open(probe_x, probe_y):
                blocked_x += math.cos(angle)
                blocked_y += math.sin(angle)
                blocked += 1
        if not blocked or (blocked_x == 0.0 and blocked_y == 0.0):
            return None
        return math.atan2(-blocked_y, -blocked_x)

    # -- integration --------------------------------------------------------
    def integrate(self, world, vacuum, command: DriveCommand, dt: float) -> Contact | None:
        """Advance one timestep. Returns the contact, if the bumper hit."""
        limits = vacuum.limits
        surface = vacuum.surface

        # 1. Motors cannot jump to the commanded speed.
        target_speed = max(-vacuum.speed, min(vacuum.speed, command.speed))
        target_turn = max(
            -limits.max_turn_rate, min(limits.max_turn_rate, command.turn)
        )
        vacuum.velocity = approach(
            vacuum.velocity, target_speed, limits.max_accel * dt, limits.max_decel * dt
        )
        vacuum.angular_velocity = approach(
            vacuum.angular_velocity,
            target_turn,
            limits.max_angular_accel * dt,
            limits.max_angular_accel * dt,
        )

        # 2. Wheels. A differential drive turns by driving the two sides at
        #    different speeds; the traction of the floor scales what actually
        #    reaches the ground.
        half_base = limits.wheel_base / 2
        vacuum.wheel_left = vacuum.velocity - vacuum.angular_velocity * half_base
        vacuum.wheel_right = vacuum.velocity + vacuum.angular_velocity * half_base

        grip = surface.traction
        speed = vacuum.velocity * grip
        turn = vacuum.angular_velocity * grip

        # 3. Deep pile grabs one wheel more than the other from step to step.
        if surface.slip and speed:
            drift = self.rng.gauss(0.0, surface.slip) * abs(speed) / max(vacuum.speed, 1e-6)
            turn += drift * limits.max_turn_rate

        # 4. Rotate, then translate. A disc is rotationally symmetric, so a
        #    turn can never push it into a wall on its own.
        vacuum.heading = wrap_angle(vacuum.heading + turn * dt)
        distance = speed * dt
        if abs(distance) < 1e-12:
            return None

        return self._translate(world, vacuum, distance)

    def _translate(self, world, vacuum, distance: float) -> Contact | None:
        """Sub-stepped motion with slide-and-rebound contact response."""
        heading = vacuum.heading if distance >= 0 else wrap_angle(vacuum.heading + math.pi)
        remaining = abs(distance)
        step_size = max(MIN_SUBSTEP, min(0.02, remaining))
        contact = None
        absorbed = False  # the bumper takes energy once per tick, not per sub-step

        while remaining > 1e-9:
            step = min(step_size, remaining)
            nx = vacuum.x + math.cos(heading) * step
            ny = vacuum.y + math.sin(heading) * step

            if world.is_free(nx, ny, vacuum.radius):
                vacuum.x, vacuum.y = nx, ny
                vacuum.distance_travelled += step
                remaining -= step
                continue

            # Probe at the position that was rejected, not the one we are
            # standing on: where the vacuum still fits, nothing reads as
            # blocked and the normal comes back empty.
            normal = self.contact_normal(world, nx, ny, vacuum.radius)
            if normal is None:
                normal = wrap_angle(heading + math.pi)
            contact = Contact(normal, abs(vacuum.velocity))

            # Split the motion into the part pushing into the surface and the
            # part running along it. The first is absorbed by the bumper, the
            # second carries on - that is what makes a glancing blow slide.
            along = wrap_angle(heading - normal)
            tangent = normal + (math.pi / 2 if along > 0 else -math.pi / 2)
            head_on = abs(math.cos(along))  # 1 = square hit, 0 = pure graze

            if not absorbed:
                # Loss goes with the square of the normal component, so a
                # square hit stops the vacuum while a graze barely slows it.
                vacuum.velocity *= 1.0 - head_on**2 * vacuum.limits.bumper_damping
                absorbed = True
            if head_on > 0.9:
                vacuum.velocity = 0.0
                vacuum.angular_velocity *= 0.4  # square hits kill the turn too
                return contact

            slide = remaining * (1.0 - head_on)
            sx = vacuum.x + math.cos(tangent) * min(slide, step)
            sy = vacuum.y + math.sin(tangent) * min(slide, step)
            if world.is_free(sx, sy, vacuum.radius):
                vacuum.x, vacuum.y = sx, sy
                vacuum.distance_travelled += min(slide, step)
                heading = tangent
                remaining -= step
            else:
                return contact
        return contact
