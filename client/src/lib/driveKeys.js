/* Manual (WASD) driving: which keys count, and what a set of held keys means. */

export const DRIVE_KEYS = [
  "w", "a", "s", "d",
  "arrowup", "arrowdown", "arrowleft", "arrowright",
];

export function isDriveKey(key) {
  return DRIVE_KEYS.includes(String(key).toLowerCase());
}

/** Held keys -> a direction on the floor plan, each axis -1, 0 or 1.

    The plan's y axis points down, so W - which drives up the plan - is -1. */
export function driveVector(held) {
  const on = (...keys) => keys.some((key) => held[key]);
  return {
    x: (on("d", "arrowright") ? 1 : 0) + (on("a", "arrowleft") ? -1 : 0),
    y: (on("s", "arrowdown") ? 1 : 0) + (on("w", "arrowup") ? -1 : 0),
  };
}
