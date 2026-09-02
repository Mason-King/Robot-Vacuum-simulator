"""Movement algorithm registry (1.6.1).

Four implementations behind one interface; `Simulation(algorithm=...)` and the
`set_algorithm` command both look names up here.
"""

from .base import MovementAlgorithm
from .manual import Manual
from .random_walk import RandomWalk
from .snaking import Snaking
from .spiral import Spiral
from .wall_following import WallFollowing

ALGORITHMS = {
    cls.name: cls for cls in (Snaking, WallFollowing, RandomWalk, Spiral, Manual)
}

DEFAULT_ALGORITHM = Snaking.name


def describe_algorithms() -> list[dict]:
    return [
        {"name": cls.name, "label": cls.label, "params": cls.describe_params()}
        for cls in ALGORITHMS.values()
    ]


__all__ = [
    "ALGORITHMS",
    "DEFAULT_ALGORITHM",
    "Manual",
    "MovementAlgorithm",
    "RandomWalk",
    "Snaking",
    "Spiral",
    "WallFollowing",
    "describe_algorithms",
]
