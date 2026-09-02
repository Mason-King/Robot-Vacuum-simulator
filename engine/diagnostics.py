"""Pre-run checks that warn the user without blocking the run (1.2.4, 1.2.5)."""

from dataclasses import dataclass

UNREACHABLE_ROOM = "unreachable_room"
PARTIAL_ROOM = "partially_unreachable_room"
VACUUM_TRAPPED = "vacuum_trapped"
PARTIAL_THRESHOLD = 0.02  # ignore a couple of cells lost to rounding


@dataclass
class Warning:
    """A problem the user should see before starting - never a hard stop."""

    code: str
    severity: str  # "warning" | "error"
    message: str
    room: str | None = None
    area: float = 0.0

    def to_dict(self) -> dict:
        return {
            "code": self.code,
            "severity": self.severity,
            "message": self.message,
            "room": self.room,
            "area": round(self.area, 2),
        }


def check_connectivity(
    world, x: float, y: float, clean_radius: float | None = None
) -> list[Warning]:
    """Flag rooms the vacuum cannot drive to from (x, y).

    Runs a flood fill over the cells the vacuum can physically occupy, so a
    doorway sealed by a blocking obstruction is caught as well as a room that
    was never connected in the first place.
    """
    grid = world.grid
    warnings: list[Warning] = []

    if not world.is_free(x, y):
        warnings.append(
            Warning(
                VACUUM_TRAPPED,
                "error",
                "The vacuum's starting position is inside a wall or a blocking "
                "obstruction - it cannot move, so the run will register 0% coverage.",
            )
        )

    radius = world.vacuum_radius if clean_radius is None else clean_radius
    reached = grid.reachable_coverage(x, y, radius)
    cell_area = grid.cell_area

    for room in world.house.rooms:
        total = blocked = 0
        for index, value in enumerate(grid.state):
            if value < 0:
                continue
            cx, cy = grid.centre(index)
            if not room.contains(cx, cy):
                continue
            total += 1
            if not reached[index]:
                blocked += 1
        if total == 0 or blocked == 0:
            continue

        lost_area = blocked * cell_area
        if blocked == total:
            warnings.append(
                Warning(
                    UNREACHABLE_ROOM,
                    "warning",
                    f"Room '{room.name}' cannot be reached from the vacuum's "
                    f"starting position - it will register 0% coverage "
                    f"({lost_area:.1f} m² of floor will never be cleaned).",
                    room.name,
                    lost_area,
                )
            )
        elif blocked / total > PARTIAL_THRESHOLD:
            share = 100.0 * blocked / total
            warnings.append(
                Warning(
                    PARTIAL_ROOM,
                    "warning",
                    f"{share:.0f}% of room '{room.name}' cannot be reached from the "
                    f"vacuum's starting position - that part will register 0% "
                    f"coverage ({lost_area:.1f} m² of floor).",
                    room.name,
                    lost_area,
                )
            )
    return warnings
