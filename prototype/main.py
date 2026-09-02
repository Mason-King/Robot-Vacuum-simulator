"""Robot vacuum simulator - room grid viewer.

Controls:
    arrow keys / WASD   move the vacuum (it cleans the tile it sits on)
    left click          paint a wall
    right click         erase back to floor
    middle click        paint furniture
    X                   sprinkle dirt on the hovered tile
    R                   regenerate the room
    G                   toggle grid lines
    ESC / window close  quit
"""

import random
import sys

import pygame

from room import DIRT_COLOR, GRID_LINE_COLOR, TILE_COLORS, Tile, default_room

COLS, ROWS = 32, 22
TILE_SIZE = 28
HUD_HEIGHT = 36
FPS = 60

WIDTH = COLS * TILE_SIZE
HEIGHT = ROWS * TILE_SIZE + HUD_HEIGHT

HUD_BG = (32, 34, 39)
HUD_FG = (226, 226, 230)
ROBOT_BODY = (44, 116, 214)
ROBOT_TRIM = (16, 60, 120)


def grid_to_px(col, row):
    return col * TILE_SIZE, row * TILE_SIZE


def px_to_grid(pos):
    x, y = pos
    return x // TILE_SIZE, y // TILE_SIZE


def draw_room(surface, room, show_grid):
    for row in range(room.rows):
        for col in range(room.cols):
            tile = room.tiles[row][col]
            rect = pygame.Rect(*grid_to_px(col, row), TILE_SIZE, TILE_SIZE)
            pygame.draw.rect(surface, TILE_COLORS[tile], rect)

            dirt = room.dirt[row][col]
            if dirt:
                # Denser speck pattern for dirtier tiles.
                specks = dirt * 3
                for i in range(specks):
                    ox = (i * 7 + 5) % (TILE_SIZE - 6) + 3
                    oy = (i * 11 + 3) % (TILE_SIZE - 6) + 3
                    pygame.draw.rect(
                        surface, DIRT_COLOR, (rect.x + ox, rect.y + oy, 2, 2)
                    )

    if show_grid:
        for col in range(room.cols + 1):
            x = col * TILE_SIZE
            pygame.draw.line(surface, GRID_LINE_COLOR, (x, 0), (x, room.rows * TILE_SIZE))
        for row in range(room.rows + 1):
            y = row * TILE_SIZE
            pygame.draw.line(surface, GRID_LINE_COLOR, (0, y), (room.cols * TILE_SIZE, y))


def draw_robot(surface, col, row):
    cx, cy = grid_to_px(col, row)
    center = (cx + TILE_SIZE // 2, cy + TILE_SIZE // 2)
    radius = TILE_SIZE // 2 - 3
    pygame.draw.circle(surface, ROBOT_BODY, center, radius)
    pygame.draw.circle(surface, ROBOT_TRIM, center, radius, 2)
    pygame.draw.circle(surface, ROBOT_TRIM, center, max(2, radius // 3))


def draw_hud(surface, font, room, hovered):
    rect = pygame.Rect(0, ROWS * TILE_SIZE, WIDTH, HUD_HEIGHT)
    pygame.draw.rect(surface, HUD_BG, rect)
    total = len(room.walkable_tiles())
    dirty = room.dirt_remaining()
    clean_pct = 100.0 if not total else 100.0 * (total - dirty) / total
    text = f"dirty tiles: {dirty}   clean: {clean_pct:5.1f}%   cursor: {hovered}   [R] new room  [G] grid"
    surface.blit(font.render(text, True, HUD_FG), (10, rect.y + 10))


def spawn_robot(room, rng):
    open_tiles = room.walkable_tiles()
    return rng.choice(open_tiles) if open_tiles else (1, 1)


def main():
    pygame.init()
    screen = pygame.display.set_mode((WIDTH, HEIGHT))
    pygame.display.set_caption("Robot Vacuum Simulator - Room Grid")
    clock = pygame.time.Clock()
    font = pygame.font.SysFont("menlo,dejavusansmono,monospace", 14)

    rng = random.Random()
    room = default_room(COLS, ROWS, rng)
    robot_col, robot_row = spawn_robot(room, rng)
    room.clean(robot_col, robot_row)
    show_grid = True

    move_keys = {
        pygame.K_LEFT: (-1, 0), pygame.K_a: (-1, 0),
        pygame.K_RIGHT: (1, 0), pygame.K_d: (1, 0),
        pygame.K_UP: (0, -1), pygame.K_w: (0, -1),
        pygame.K_DOWN: (0, 1), pygame.K_s: (0, 1),
    }

    running = True
    while running:
        mouse_col, mouse_row = px_to_grid(pygame.mouse.get_pos())
        hovered = (mouse_col, mouse_row) if room.in_bounds(mouse_col, mouse_row) else None

        for event in pygame.event.get():
            if event.type == pygame.QUIT:
                running = False
            elif event.type == pygame.KEYDOWN:
                if event.key == pygame.K_ESCAPE:
                    running = False
                elif event.key == pygame.K_r:
                    room = default_room(COLS, ROWS, rng)
                    robot_col, robot_row = spawn_robot(room, rng)
                    room.clean(robot_col, robot_row)
                elif event.key == pygame.K_g:
                    show_grid = not show_grid
                elif event.key == pygame.K_x and hovered:
                    room.add_dirt(mouse_col, mouse_row)
                if event.key in move_keys:
                    dx, dy = move_keys[event.key]
                    nc, nr = robot_col + dx, robot_row + dy
                    if room.is_walkable(nc, nr):
                        robot_col, robot_row = nc, nr
                        room.clean(robot_col, robot_row)

        if hovered:
            left, middle, right = pygame.mouse.get_pressed(3)
            if left:
                room.set_tile(mouse_col, mouse_row, Tile.WALL)
            elif right:
                room.set_tile(mouse_col, mouse_row, Tile.FLOOR)
            elif middle:
                room.set_tile(mouse_col, mouse_row, Tile.FURNITURE)

        draw_room(screen, room, show_grid)
        draw_robot(screen, robot_col, robot_row)
        draw_hud(screen, font, room, hovered)
        pygame.display.flip()
        clock.tick(FPS)

    pygame.quit()
    sys.exit(0)


if __name__ == "__main__":
    main()
