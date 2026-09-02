"""Floor coverings (1.5.2, 1.7.7, 1.7.8).

One covering applies to the whole house for now. Each has a default cleaning
efficiency - how much of a cell's dirt one pass removes - which the user may
override anywhere inside EFFICIENCY_BOUNDS.
"""

from dataclasses import dataclass

EFFICIENCY_BOUNDS = (0.05, 1.0)


@dataclass(frozen=True)
class FloorCovering:
    name: str
    label: str
    default_efficiency: float
    color: str  # the client draws the floor with this
    traction: float = 1.0  # share of commanded motion that reaches the floor
    slip: float = 0.0      # random wheel slip, as a fraction of full turn rate


COVERINGS = {
    covering.name: covering
    for covering in (
        FloorCovering("hardwood", "Hardwood", 0.95, "#6b5136", 1.00, 0.000),
        FloorCovering("tile", "Tile", 0.90, "#4a5a63", 0.98, 0.004),
        FloorCovering("low_pile", "Low-pile carpet", 0.65, "#4d4a5e", 0.90, 0.030),
        FloorCovering("high_pile", "High-pile carpet", 0.40, "#5a4450", 0.76, 0.090),
    )
}

DEFAULT_COVERING = "hardwood"


def clamp_efficiency(value: float) -> float:
    low, high = EFFICIENCY_BOUNDS
    return max(low, min(high, float(value)))


def coverings_dict() -> list[dict]:
    return [
        {
            "name": c.name,
            "label": c.label,
            "defaultEfficiency": c.default_efficiency,
            "color": c.color,
            "traction": c.traction,
            "slip": c.slip,
        }
        for c in COVERINGS.values()
    ]
