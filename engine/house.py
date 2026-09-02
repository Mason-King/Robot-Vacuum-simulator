"""House geometry. Rooms are axis-aligned rectangles in metres."""

from collections import deque
from dataclasses import dataclass


@dataclass
class Room:
    name: str
    x: float
    y: float
    width: float
    height: float
    #: Floor covering name, or None to inherit the house default (1.5.2).
    covering: str | None = None

    @property
    def x2(self) -> float:
        return self.x + self.width

    @property
    def y2(self) -> float:
        return self.y + self.height

    def contains(self, x: float, y: float, margin: float = 0.0) -> bool:
        return (
            self.x + margin <= x <= self.x2 - margin
            and self.y + margin <= y <= self.y2 - margin
        )

    def to_dict(self) -> dict:
        return {
            "name": self.name,
            "x": self.x,
            "y": self.y,
            "width": self.width,
            "height": self.height,
            "covering": self.covering,
        }


class House:
    """A collection of rooms. Walkable space is the union of the rectangles."""

    def __init__(self, rooms: list[Room]):
        if not rooms:
            raise ValueError("a house needs at least one room")
        self.rooms = rooms

    # -- editing -----------------------------------------------------------
    def find(self, name: str) -> Room | None:
        return next((room for room in self.rooms if room.name == name), None)

    def unique_name(self, base: str) -> str:
        if self.find(base) is None:
            return base
        n = 2
        while self.find(f"{base} {n}") is not None:
            n += 1
        return f"{base} {n}"

    def add_room(self, room: Room) -> Room:
        room.name = self.unique_name(room.name or "room")
        self.rooms.append(room)
        return room

    def remove_room(self, name: str) -> bool:
        """Delete a room. The house must keep at least one."""
        room = self.find(name)
        if room is None or len(self.rooms) == 1:
            return False
        self.rooms.remove(room)
        return True

    def is_walkable(self, x: float, y: float, margin: float = 0.0) -> bool:
        return any(room.contains(x, y, margin) for room in self.rooms)

    def room_at(self, x: float, y: float) -> Room | None:
        for room in self.rooms:
            if room.contains(x, y):
                return room
        return None

    def bounds(self) -> tuple[float, float, float, float]:
        """(min_x, min_y, max_x, max_y) over every room."""
        return (
            min(r.x for r in self.rooms),
            min(r.y for r in self.rooms),
            max(r.x2 for r in self.rooms),
            max(r.y2 for r in self.rooms),
        )

    # -- adjacency -------------------------------------------------------
    # Two rooms are connected when their rectangles overlap; the centre of
    # that overlap is treated as the doorway between them. Because rooms are
    # convex, a straight line from anywhere in a room to one of its doorways
    # always stays inside that room - which is what lets the navigation below
    # get away with moving in straight lines.

    @staticmethod
    def overlap(a: Room, b: Room) -> tuple[float, float, float, float] | None:
        x1, y1 = max(a.x, b.x), max(a.y, b.y)
        x2, y2 = min(a.x2, b.x2), min(a.y2, b.y2)
        if x2 - x1 <= 0 or y2 - y1 <= 0:
            return None
        return (x1, y1, x2, y2)

    def doorway(self, a: Room, b: Room) -> tuple[float, float] | None:
        rect = self.overlap(a, b)
        if rect is None:
            return None
        x1, y1, x2, y2 = rect
        return ((x1 + x2) / 2, (y1 + y2) / 2)

    def neighbours(self, room: Room) -> list[Room]:
        return [
            other
            for other in self.rooms
            if other is not room and self.overlap(room, other) is not None
        ]

    def route(self, start: Room, goal: Room) -> list[tuple[float, float]]:
        """Doorway waypoints leading from `start` into `goal` (BFS over rooms)."""
        if start is goal:
            return []
        previous: dict[str, Room | None] = {start.name: None}
        queue = deque([start])
        while queue:
            room = queue.popleft()
            if room is goal:
                break
            for neighbour in self.neighbours(room):
                if neighbour.name not in previous:
                    previous[neighbour.name] = room
                    queue.append(neighbour)
        if goal.name not in previous:
            return []

        chain = [goal]
        while (parent := previous[chain[-1].name]) is not None:
            chain.append(parent)
        chain.reverse()

        waypoints = []
        for a, b in zip(chain, chain[1:]):
            door = self.doorway(a, b)
            if door is not None:
                waypoints.append(door)
        return waypoints

    def to_dict(self) -> dict:
        min_x, min_y, max_x, max_y = self.bounds()
        return {
            "rooms": [room.to_dict() for room in self.rooms],
            "bounds": {"minX": min_x, "minY": min_y, "maxX": max_x, "maxY": max_y},
        }


def default_house() -> House:
    """A small two-bedroom flat: rooms overlap slightly to act as doorways."""
    return House(
        [
            Room("living", 0.0, 0.0, 6.0, 5.0, "low_pile"),
            Room("hall", 5.8, 1.5, 2.4, 1.6),
            Room("kitchen", 8.0, 0.0, 4.0, 3.4, "tile"),
            Room("bedroom", 8.0, 3.2, 4.0, 4.0, "high_pile"),
            Room("study", 0.0, 4.6, 3.6, 3.4),
        ]
    )
