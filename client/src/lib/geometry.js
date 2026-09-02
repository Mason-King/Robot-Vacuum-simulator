/* Pure geometry helpers shared by the canvas and the editing tools.
   Nothing here touches Vue or the DOM, so it is trivial to test. */

export const SNAP = 0.1; // metres
export const MIN_ROOM_SIDE = 0.5;
export const GRIP_PX = 12;

export function snap(value) {
  return Math.round(value / SNAP) * SNAP;
}

/** Fit the house bounds to a canvas, returning both directions of the mapping. */
export function createProjection({ width, height }, bounds, pad = 26) {
  const worldWidth = bounds.maxX - bounds.minX;
  const worldHeight = bounds.maxY - bounds.minY;
  const scale = Math.min(
    (width - pad * 2) / worldWidth,
    (height - pad * 2) / worldHeight
  );
  const ox = (width - worldWidth * scale) / 2 - bounds.minX * scale;
  const oy = (height - worldHeight * scale) / 2 - bounds.minY * scale;

  return {
    scale,
    ox,
    oy,
    px: (x) => x * scale + ox,
    py: (y) => y * scale + oy,
    toWorld: (px, py) => ({ x: (px - ox) / scale, y: (py - oy) / scale }),
  };
}

function inside(rect, x, y) {
  return (
    x >= rect.x && x <= rect.x + rect.width && y >= rect.y && y <= rect.y + rect.height
  );
}

/** Obstructions sit on top of rooms, and later rooms on top of earlier ones. */
export function hitTest({ rooms = [], obstructions = [] }, x, y) {
  for (let i = obstructions.length - 1; i >= 0; i--) {
    if (inside(obstructions[i], x, y)) {
      return { type: "obstruction", id: obstructions[i].id, rect: obstructions[i] };
    }
  }
  for (let i = rooms.length - 1; i >= 0; i--) {
    if (inside(rooms[i], x, y)) {
      return { type: "room", id: rooms[i].name, rect: rooms[i] };
    }
  }
  return null;
}

/** The resize grip is the bottom-right corner of the selected rectangle. */
export function isOnGrip(x, y, rect, scale) {
  if (!rect) return false;
  const reach = GRIP_PX / scale;
  return (
    Math.abs(x - (rect.x + rect.width)) < reach &&
    Math.abs(y - (rect.y + rect.height)) < reach
  );
}

/** Turn a drag (which may run right-to-left or bottom-to-top) into a rectangle. */
export function normaliseRect(draft) {
  return {
    x: snap(Math.min(draft.ox, draft.ox + draft.width)),
    y: snap(Math.min(draft.oy, draft.oy + draft.height)),
    width: snap(Math.abs(draft.width)),
    height: snap(Math.abs(draft.height)),
  };
}
