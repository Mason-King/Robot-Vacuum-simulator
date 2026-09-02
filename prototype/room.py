"""Grid model for the robot vacuum simulator room."""

from enum import IntEnum


class Tile(IntEnum):
    FLOOR = 0
    WALL = 1
    FURNITURE = 2


# Colors indexed by Tile
TILE_COLORS = {
    Tile.FLOOR: (238, 234, 226),
    Tile.WALL: (60, 63, 70),
    Tile.FURNITURE: (150, 111, 78),
}

GRID_LINE_COLOR = (214, 209, 199)
DIRT_COLOR = (128, 106, 78)


class Room:
    """A rectangular room laid out on a tile grid.

    Tiles hold terrain (floor / wall / furniture); dirt is tracked separately
    so it can be cleared without touching the layout.
    """

    def __init__(self, cols, rows):
        self.cols = cols
        self.rows = rows
        self.tiles = [[Tile.FLOOR] * cols for _ in range(rows)]
        self.dirt = [[0] * cols for _ in range(rows)]
        self._build_walls()

    def _build_walls(self):
        for x in range(self.cols):
            self.tiles[0][x] = Tile.WALL
            self.tiles[self.rows - 1][x] = Tile.WALL
        for y in range(self.rows):
            self.tiles[y][0] = Tile.WALL
            self.tiles[y][self.cols - 1] = Tile.WALL

    def in_bounds(self, col, row):
        return 0 <= col < self.cols and 0 <= row < self.rows

    def is_walkable(self, col, row):
        return self.in_bounds(col, row) and self.tiles[row][col] == Tile.FLOOR

    def set_tile(self, col, row, tile):
        if not self.in_bounds(col, row):
            return
        self.tiles[row][col] = tile
        if tile != Tile.FLOOR:
            self.dirt[row][col] = 0

    def fill_rect(self, col, row, w, h, tile):
        for y in range(row, row + h):
            for x in range(col, col + w):
                self.set_tile(x, y, tile)

    def add_dirt(self, col, row, amount=1):
        if self.is_walkable(col, row):
            self.dirt[row][col] = min(3, self.dirt[row][col] + amount)

    def clean(self, col, row):
        """Remove dirt at a tile. Returns True if anything was cleaned."""
        if self.in_bounds(col, row) and self.dirt[row][col]:
            self.dirt[row][col] = 0
            return True
        return False

    def dirt_remaining(self):
        return sum(1 for row in self.dirt for d in row if d)

    def walkable_tiles(self):
        return [
            (x, y)
            for y in range(self.rows)
            for x in range(self.cols)
            if self.tiles[y][x] == Tile.FLOOR
        ]


def default_room(cols, rows, rng):
    """A room with a couple of interior walls, furniture, and scattered dirt."""
    room = Room(cols, rows)

    # Interior partition wall with a doorway
    wall_x = cols // 2
    for y in range(1, rows - 1):
        room.set_tile(wall_x, y, Tile.WALL)
    doorway = rows // 2
    for y in (doorway - 1, doorway, doorway + 1):
        room.set_tile(wall_x, y, Tile.FLOOR)

    # Furniture: a couch, a table, a bed-ish block
    room.fill_rect(2, 2, 4, 2, Tile.FURNITURE)
    room.fill_rect(3, rows - 6, 3, 3, Tile.FURNITURE)
    room.fill_rect(wall_x + 3, 3, 4, 4, Tile.FURNITURE)
    room.fill_rect(cols - 4, rows - 5, 2, 3, Tile.FURNITURE)

    # Scatter dirt over roughly a third of the open floor
    open_tiles = room.walkable_tiles()
    for col, row in rng.sample(open_tiles, len(open_tiles) // 3):
        room.add_dirt(col, row, rng.randint(1, 3))

    return room
