"""Grid-based coverage tracking (1.7.9, 1.7.10).

The house is overlaid with a uniform grid. Every cell is one of:

    UNCLEANABLE   outside the rooms, or under a blocking obstruction
    0.0 - 1.0     cleanliness: 0 never touched, 1 fully cleaned

A pass deposits `efficiency * distance / cell_size` of cleaning, so a slow
sweep over high-pile carpet needs several passes to finish a cell while one
pass over hardwood very nearly does it.
"""

from collections import deque

UNCLEANABLE = -1.0
DEFAULT_CELL_SIZE = 0.1


class CoverageGrid:
    def __init__(self, house, cell_size: float = DEFAULT_CELL_SIZE):
        min_x, min_y, max_x, max_y = house.bounds()
        self.cell_size = cell_size
        self.origin_x = min_x
        self.origin_y = min_y
        self.cols = max(1, int(round((max_x - min_x) / cell_size)))
        self.rows = max(1, int(round((max_y - min_y) / cell_size)))
        self.state = [UNCLEANABLE] * (self.cols * self.rows)
        self.passable = bytearray(self.cols * self.rows)
        self._dirty: set[int] = set()
        self.non_cleanable_area = 0.0

    # -- geometry ---------------------------------------------------------
    @property
    def cell_area(self) -> float:
        return self.cell_size * self.cell_size

    def index(self, x: float, y: float) -> int | None:
        col = int((x - self.origin_x) / self.cell_size)
        row = int((y - self.origin_y) / self.cell_size)
        if 0 <= col < self.cols and 0 <= row < self.rows:
            return row * self.cols + col
        return None

    def centre(self, index: int) -> tuple[float, float]:
        col, row = index % self.cols, index // self.cols
        half = self.cell_size / 2
        return (self.origin_x + col * self.cell_size + half,
                self.origin_y + row * self.cell_size + half)

    # -- (re)building ------------------------------------------------------
    def rebuild(self, in_house, cleanable, passable, keep_progress: bool = True) -> None:
        """Recompute cleanable/passable masks; existing progress is preserved."""
        previous = self.state if keep_progress else None
        self.state = [UNCLEANABLE] * (self.cols * self.rows)
        self.passable = bytearray(self.cols * self.rows)
        blocked_cells = 0

        for index in range(len(self.state)):
            x, y = self.centre(index)
            if not in_house(x, y):
                continue
            if cleanable(x, y):
                was = previous[index] if previous is not None else UNCLEANABLE
                self.state[index] = was if was != UNCLEANABLE else 0.0
            else:
                blocked_cells += 1
            if passable(x, y):
                self.passable[index] = 1

        self.non_cleanable_area = blocked_cells * self.cell_area
        self._dirty = set(range(len(self.state)))  # clients must resync

    # -- cleaning ----------------------------------------------------------
    def apply(self, cx: float, cy: float, radius: float, dose: float) -> None:
        if dose <= 0:
            return
        span = int(radius / self.cell_size) + 1
        centre = self.index(cx, cy)
        if centre is None:
            return
        col0, row0 = centre % self.cols, centre // self.cols
        radius_sq = radius * radius

        for row in range(max(0, row0 - span), min(self.rows, row0 + span + 1)):
            for col in range(max(0, col0 - span), min(self.cols, col0 + span + 1)):
                index = row * self.cols + col
                value = self.state[index]
                if value == UNCLEANABLE or value >= 1.0:
                    continue
                x, y = self.centre(index)
                if (x - cx) ** 2 + (y - cy) ** 2 > radius_sq:
                    continue
                self.state[index] = min(1.0, value + dose)
                self._dirty.add(index)

    # -- reachability (1.2.4) ----------------------------------------------
    def reachable_from(self, x: float, y: float) -> bytearray:
        """Flood fill over passable cells; 1 where the vacuum can actually get."""
        reached = bytearray(len(self.state))
        start = self.index(x, y)
        if start is None or not self.passable[start]:
            start = self._nearest_passable(x, y)
        if start is None:
            return reached

        reached[start] = 1
        queue = deque([start])
        while queue:
            index = queue.popleft()
            col, row = index % self.cols, index // self.cols
            for dcol, drow in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ncol, nrow = col + dcol, row + drow
                if not (0 <= ncol < self.cols and 0 <= nrow < self.rows):
                    continue
                neighbour = nrow * self.cols + ncol
                if reached[neighbour] or not self.passable[neighbour]:
                    continue
                reached[neighbour] = 1
                queue.append(neighbour)
        return reached

    def reachable_coverage(self, x: float, y: float, radius: float) -> bytearray:
        """Cells the vacuum could ever clean: reachable positions, dilated by
        the cleaning radius. A cell hugging a wall is cleanable even though the
        vacuum's centre can never sit on it."""
        reached = self.reachable_from(x, y)
        span = int(radius / self.cell_size) + 1
        offsets = [
            (dcol, drow)
            for drow in range(-span, span + 1)
            for dcol in range(-span, span + 1)
            if (dcol * self.cell_size) ** 2 + (drow * self.cell_size) ** 2
            <= radius * radius
        ]

        covered = bytearray(len(self.state))
        for index, ok in enumerate(reached):
            if not ok:
                continue
            col, row = index % self.cols, index // self.cols
            for dcol, drow in offsets:
                ncol, nrow = col + dcol, row + drow
                if 0 <= ncol < self.cols and 0 <= nrow < self.rows:
                    covered[nrow * self.cols + ncol] = 1
        return covered

    def _nearest_passable(self, x: float, y: float) -> int | None:
        best, best_distance = None, float("inf")
        for index, ok in enumerate(self.passable):
            if not ok:
                continue
            cx, cy = self.centre(index)
            distance = (cx - x) ** 2 + (cy - y) ** 2
            if distance < best_distance:
                best, best_distance = index, distance
        return best

    # -- reporting ----------------------------------------------------------
    def stats(self) -> dict:
        cleanable = visited = cleaned = 0
        total = 0.0
        for value in self.state:
            if value == UNCLEANABLE:
                continue
            cleanable += 1
            total += value
            if value > 0.0:
                visited += 1
            if value >= 1.0:
                cleaned += 1
        percent = (100.0 * visited / cleanable) if cleanable else 0.0
        cleaned_percent = (100.0 * cleaned / cleanable) if cleanable else 0.0
        return {
            "percent": round(percent, 2),
            "cleanedPercent": round(cleaned_percent, 2),
            "meanCleanliness": round(100.0 * total / cleanable, 2) if cleanable else 0.0,
            "cellsCleanable": cleanable,
            "cellsVisited": visited,
            "cellsCleaned": cleaned,
            "cleanableArea": round(cleanable * self.cell_area, 3),
            "nonCleanableArea": round(self.non_cleanable_area, 3),
        }

    def drain_delta(self) -> list[int]:
        """Flat [index, percent, index, percent, ...] of cells changed since last call."""
        delta: list[int] = []
        for index in self._dirty:
            value = self.state[index]
            delta.append(index)
            delta.append(-1 if value == UNCLEANABLE else int(round(value * 100)))
        self._dirty.clear()
        return delta

    def snapshot(self) -> list[int]:
        self._dirty.clear()
        return [-1 if v == UNCLEANABLE else int(round(v * 100)) for v in self.state]

    def to_dict(self) -> dict:
        return {
            "cellSize": self.cell_size,
            "cols": self.cols,
            "rows": self.rows,
            "originX": self.origin_x,
            "originY": self.origin_y,
        }
