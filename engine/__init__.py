"""Robot vacuum simulation engine - headless, no I/O, no rendering."""

from .algorithms import ALGORITHMS, DEFAULT_ALGORITHM, MovementAlgorithm
from .coverage import CoverageGrid
from .diagnostics import Warning, check_connectivity
from .floor import COVERINGS, FloorCovering
from .house import House, Room, default_house
from .navigation import World
from .obstruction import BLOCKING, PASS_UNDER, Obstruction, default_obstructions
from .simulation import DEFAULT_DT, Simulation
from .vacuum import Battery, Vacuum

__all__ = [
    "ALGORITHMS",
    "BLOCKING",
    "COVERINGS",
    "DEFAULT_ALGORITHM",
    "DEFAULT_DT",
    "PASS_UNDER",
    "Battery",
    "CoverageGrid",
    "FloorCovering",
    "House",
    "MovementAlgorithm",
    "Obstruction",
    "Room",
    "Simulation",
    "Vacuum",
    "Warning",
    "World",
    "check_connectivity",
    "default_house",
    "default_obstructions",
]
