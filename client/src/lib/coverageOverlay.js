/* The coverage overlay is drawn into an offscreen canvas at one pixel per grid
   cell, then blown up with smoothing off. Repainting thousands of rectangles a
   frame would cost far more than blitting one image. */

/** Uncleaned floor shows through; a cell ramps through amber, then turns green. */
export function coverageColor(percent) {
  if (percent <= 0) return [0, 0, 0, 0]; // untouched, or outside the house
  if (percent >= 100) return [70, 192, 122, 165];
  return [185, 139, 62, 60 + Math.round((percent / 100) * 90)];
}

export class CoverageOverlay {
  constructor(cols, rows) {
    this.cols = cols;
    this.rows = rows;
    this.canvas = document.createElement("canvas");
    this.canvas.width = cols;
    this.canvas.height = rows;
    this.ctx = this.canvas.getContext("2d");
    this.image = this.ctx.createImageData(cols, rows);
    this.dirty = false;
  }

  /** Repaint every cell from a full snapshot. */
  build(cells) {
    for (let i = 0; i < cells.length; i++) this.write(i, cells[i]);
    this.ctx.putImageData(this.image, 0, 0);
    this.dirty = false;
  }

  /** Apply a flat [index, percent, index, percent, ...] delta. */
  applyDelta(delta) {
    for (let i = 0; i < delta.length; i += 2) this.write(delta[i], delta[i + 1]);
    this.dirty = delta.length > 0;
  }

  write(index, percent) {
    const [r, g, b, a] = coverageColor(percent);
    const p = index * 4;
    this.image.data[p] = r;
    this.image.data[p + 1] = g;
    this.image.data[p + 2] = b;
    this.image.data[p + 3] = a;
  }

  /** Push pending writes to the offscreen canvas, once per frame. */
  flush() {
    if (!this.dirty) return;
    this.ctx.putImageData(this.image, 0, 0);
    this.dirty = false;
  }
}
