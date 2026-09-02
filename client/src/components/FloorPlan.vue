<script setup>
import { onBeforeUnmount, onMounted, ref, watch } from "vue";

import { CoverageOverlay } from "@/lib/coverageOverlay";
import { DRIVE_KEYS, driveVector, isDriveKey } from "@/lib/driveKeys";
import {
  MIN_ROOM_SIDE,
  createProjection,
  hitTest,
  isOnGrip,
  normaliseRect,
  snap,
} from "@/lib/geometry";
import { useEditor } from "@/stores/editor";
import { useSimulation } from "@/stores/simulation";

const COLORS = {
  wall: "#8b97a5",
  label: "#8590a0",
  grid: "rgba(139, 151, 165, 0.13)",
  gridMajor: "rgba(139, 151, 165, 0.28)",
  blocking: "#8d4a45",
  blockingEdge: "#c37b74",
  passUnder: "#9aa6b4",
  body: "#4d9bff",
  bodyEdge: "#12294a",
  selection: "#4d9bff",
  draftRoom: "#46c07a",
  draftObstruction: "#e0a75e",
};

const sim = useSimulation();
const { state, raw, isManual } = sim;
const { editor, selected, select, syncForm, commitForm, deleteSelection } = useEditor();

const canvas = ref(null);
const cursor = ref("default");
const patterns = new Map();

let overlay = null;
let view = null;
let drag = null;
let draft = ref(null);
const held = {};

/* Carpet gets a dot texture over its colour, so coverings are distinguishable
   by more than hue alone (1.5.3). One cached pattern per covering. */
function floorFill(ctx, covering) {
  if (!covering) return "#6b5136";
  if (!covering.name.includes("pile")) return covering.color;
  if (patterns.has(covering.name)) return patterns.get(covering.name);

  const tile = document.createElement("canvas");
  tile.width = tile.height = 8;
  const tctx = tile.getContext("2d");
  tctx.fillStyle = covering.color;
  tctx.fillRect(0, 0, 8, 8);
  tctx.fillStyle = "rgba(255,255,255,0.07)";
  tctx.fillRect(0, 0, 2, 2);
  tctx.fillRect(4, 4, 2, 2);
  const pattern = ctx.createPattern(tile, "repeat");
  patterns.set(covering.name, pattern);
  return pattern;
}

// -- rendering ---------------------------------------------------------------
function resize() {
  const el = canvas.value;
  if (!el) return;
  const ratio = window.devicePixelRatio || 1;
  const rect = el.getBoundingClientRect();
  el.width = Math.max(1, Math.round(rect.width * ratio));
  el.height = Math.max(1, Math.round(rect.height * ratio));
  draw();
}

function drawGrid(ctx, view) {
  const { grid, house } = state;
  if (!grid) return;
  const { minX, minY, maxX, maxY } = house.bounds;
  const cellPx = grid.cellSize * view.scale;

  // Cell lines only once they are far enough apart to read.
  if (cellPx >= 5) {
    ctx.strokeStyle = COLORS.grid;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = minX; x <= maxX + 1e-9; x += grid.cellSize) {
      ctx.moveTo(view.px(x), view.py(minY));
      ctx.lineTo(view.px(x), view.py(maxY));
    }
    for (let y = minY; y <= maxY + 1e-9; y += grid.cellSize) {
      ctx.moveTo(view.px(minX), view.py(y));
      ctx.lineTo(view.px(maxX), view.py(y));
    }
    ctx.stroke();
  }

  // One metre majors, always.
  ctx.strokeStyle = COLORS.gridMajor;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let x = Math.ceil(minX); x <= maxX + 1e-9; x += 1) {
    ctx.moveTo(view.px(x), view.py(minY));
    ctx.lineTo(view.px(x), view.py(maxY));
  }
  for (let y = Math.ceil(minY); y <= maxY + 1e-9; y += 1) {
    ctx.moveTo(view.px(minX), view.py(y));
    ctx.lineTo(view.px(maxX), view.py(y));
  }
  ctx.stroke();
}

function draw() {
  const el = canvas.value;
  if (!el || !state.house) return;
  const ctx = el.getContext("2d");
  view = createProjection(el, state.house.bounds);
  const { scale, px, py } = view;

  ctx.clearRect(0, 0, el.width, el.height);

  // Floor: each room in its own covering, so a carpeted bedroom next to a
  // tiled kitchen reads as two surfaces rather than one house.
  for (const room of state.house.rooms) {
    const covering = sim.coveringFor(room);
    ctx.fillStyle = floorFill(ctx, covering);
    ctx.fillRect(px(room.x), py(room.y), room.width * scale, room.height * scale);
  }

  // Live coverage overlay
  if (overlay && state.grid) {
    overlay.flush();
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(
      overlay.canvas,
      px(state.grid.originX),
      py(state.grid.originY),
      state.grid.cols * state.grid.cellSize * scale,
      state.grid.rows * state.grid.cellSize * scale
    );
    ctx.imageSmoothingEnabled = true;
  }

  if (editor.showGrid) drawGrid(ctx, view);

  // Walls and room names
  ctx.strokeStyle = COLORS.wall;
  ctx.lineWidth = Math.max(1.5, scale * 0.055);
  for (const room of state.house.rooms) {
    ctx.strokeRect(px(room.x), py(room.y), room.width * scale, room.height * scale);
  }
  ctx.fillStyle = COLORS.label;
  ctx.font = `${Math.round(scale * 0.2)}px system-ui, sans-serif`;
  for (const room of state.house.rooms) {
    ctx.fillText(room.name, px(room.x) + 7, py(room.y) + scale * 0.3);
  }

  // Obstructions: solid for blocking, dashed outline for pass-under
  for (const o of state.obstructions) {
    const x = px(o.x);
    const y = py(o.y);
    const w = o.width * scale;
    const h = o.height * scale;
    if (o.kind === "blocking") {
      ctx.fillStyle = COLORS.blocking;
      ctx.fillRect(x, y, w, h);
      ctx.strokeStyle = COLORS.blockingEdge;
      ctx.setLineDash([]);
    } else {
      ctx.fillStyle = "rgba(154, 166, 180, 0.14)";
      ctx.fillRect(x, y, w, h);
      ctx.strokeStyle = COLORS.passUnder;
      ctx.setLineDash([6, 4]);
    }
    ctx.lineWidth = 1.5;
    ctx.strokeRect(x, y, w, h);
    ctx.setLineDash([]);
    if (o.label) {
      ctx.fillStyle = "rgba(255,255,255,0.7)";
      ctx.font = `${Math.round(scale * 0.14)}px system-ui, sans-serif`;
      ctx.fillText(o.label, x + 4, y + scale * 0.22);
    }
  }

  // Selection outline and its resize grip
  if (selected.value) {
    const s = selected.value;
    const x = px(s.x);
    const y = py(s.y);
    const w = s.width * scale;
    const h = s.height * scale;
    ctx.strokeStyle = COLORS.selection;
    ctx.lineWidth = 2;
    ctx.setLineDash([5, 3]);
    ctx.strokeRect(x, y, w, h);
    ctx.setLineDash([]);
    ctx.fillStyle = COLORS.selection;
    ctx.fillRect(x + w - 5, y + h - 5, 10, 10);
  }

  // Rectangle being drawn right now
  if (draft.value) {
    const d = normaliseRect(draft.value);
    ctx.strokeStyle =
      editor.tool === "draw_room" ? COLORS.draftRoom : COLORS.draftObstruction;
    ctx.lineWidth = 2;
    ctx.setLineDash([6, 4]);
    ctx.strokeRect(px(d.x), py(d.y), d.width * scale, d.height * scale);
    ctx.setLineDash([]);
    ctx.fillStyle = "rgba(255,255,255,0.75)";
    ctx.font = `${Math.round(scale * 0.16)}px system-ui, sans-serif`;
    ctx.fillText(`${d.width.toFixed(1)} x ${d.height.toFixed(1)} m`, px(d.x) + 5, py(d.y) - 6);
  }

  // Bumper contact: a bright arc on the side that touched
  if (state.vacuum && state.vacuum.touching && state.vacuum.contact) {
    const { x, y, radius } = state.vacuum;
    const facing = state.vacuum.contact.normal + Math.PI; // toward the surface
    ctx.strokeStyle = "#e0605e";
    ctx.lineWidth = Math.max(2, radius * scale * 0.45);
    ctx.beginPath();
    ctx.arc(px(x), py(y), radius * scale, facing - 0.8, facing + 0.8);
    ctx.stroke();
  }

  // The vacuum: brush reach, body, heading
  if (state.vacuum) {
    const { x, y, radius, cleanRadius, heading } = state.vacuum;
    const cx = px(x);
    const cy = py(y);
    ctx.strokeStyle = "rgba(77, 155, 255, 0.35)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(cx, cy, (cleanRadius || radius) * scale, 0, Math.PI * 2);
    ctx.stroke();
    ctx.fillStyle = COLORS.body;
    ctx.beginPath();
    ctx.arc(cx, cy, radius * scale, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = COLORS.bodyEdge;
    ctx.lineWidth = Math.max(1, radius * scale * 0.2);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(cx, cy);
    ctx.lineTo(px(x + Math.cos(heading) * radius), py(y + Math.sin(heading) * radius));
    ctx.stroke();
  }
}

// -- pointer interaction -----------------------------------------------------
function toWorld(event) {
  const rect = canvas.value.getBoundingClientRect();
  const ratio = window.devicePixelRatio || 1;
  return view.toWorld(
    (event.clientX - rect.left) * ratio,
    (event.clientY - rect.top) * ratio
  );
}

function onPointerDown(event) {
  if (!view || !state.house) return;
  const { x, y } = toWorld(event);

  if (editor.tool !== "select") {
    draft.value = { x, y, width: 0, height: 0, ox: x, oy: y };
    return;
  }

  if (selected.value && isOnGrip(x, y, selected.value, view.scale)) {
    drag = { kind: "resize", ox: x, oy: y, start: { ...selected.value } };
    return;
  }

  const hit = hitTest(
    { rooms: state.house.rooms, obstructions: state.obstructions },
    x,
    y
  );
  select(hit);
  if (hit) drag = { kind: "move", ox: x, oy: y, start: { ...hit.rect } };
}

function onPointerMove(event) {
  if (!view || !state.house) return;
  const { x, y } = toWorld(event);

  if (draft.value) {
    draft.value.width = x - draft.value.ox;
    draft.value.height = y - draft.value.oy;
    draw();
    return;
  }

  if (drag && selected.value) {
    const target = selected.value;
    const dx = x - drag.ox;
    const dy = y - drag.oy;
    if (drag.kind === "move") {
      target.x = snap(drag.start.x + dx);
      target.y = snap(drag.start.y + dy);
    } else {
      target.width = Math.max(MIN_ROOM_SIDE, snap(drag.start.width + dx));
      target.height = Math.max(MIN_ROOM_SIDE, snap(drag.start.height + dy));
    }
    syncForm();
    draw();
    return;
  }

  cursor.value =
    editor.tool !== "select"
      ? "crosshair"
      : selected.value && isOnGrip(x, y, selected.value, view.scale)
        ? "nwse-resize"
        : hitTest({ rooms: state.house.rooms, obstructions: state.obstructions }, x, y)
          ? "move"
          : "default";
}

function onPointerUp() {
  if (draft.value) {
    const rect = normaliseRect(draft.value);
    const tiny = rect.width < 0.4 || rect.height < 0.4;
    if (editor.tool === "draw_room") {
      if (!tiny) sim.addRoom({ name: "room", ...rect });
    } else {
      // A plain click drops a default-sized obstruction on the spot.
      const box = tiny
        ? {
            x: snap(draft.value.ox - editor.obstruction.width / 2),
            y: snap(draft.value.oy - editor.obstruction.height / 2),
            width: editor.obstruction.width,
            height: editor.obstruction.height,
          }
        : rect;
      sim.placeObstruction({ ...box, kind: editor.obstruction.kind });
    }
    draft.value = null;
    editor.tool = "select";
    draw();
    return;
  }

  if (drag) {
    drag = null;
    commitForm();
  }
}

// -- keyboard ----------------------------------------------------------------
function updateDrive() {
  const { x, y } = driveVector(held);
  if (x === held._x && y === held._y) return; // key repeat sends nothing new
  held._x = x;
  held._y = y;
  sim.setDrive(x, y);
}

function onKeyDown(event) {
  const tag = (event.target.tagName || "").toLowerCase();
  if (["input", "select", "textarea"].includes(tag)) return;

  const key = String(event.key).toLowerCase();
  if (isManual.value && isDriveKey(key)) {
    event.preventDefault(); // arrow keys would otherwise scroll the page
    held[key] = true;
    updateDrive();
    return;
  }

  if (event.key === "Escape") {
    draft.value = null;
    drag = null;
    editor.tool = "select";
    draw();
  } else if (event.key === "Delete" || event.key === "Backspace") {
    event.preventDefault();
    deleteSelection();
  }
}

function onKeyUp(event) {
  const key = String(event.key).toLowerCase();
  if (!isDriveKey(key)) return;
  delete held[key];
  updateDrive();
}

function releaseKeys() {
  for (const key of DRIVE_KEYS) delete held[key];
  updateDrive();
}

// -- wiring ------------------------------------------------------------------
watch(
  () => state.worldVersion,
  () => {
    if (!state.grid) return;
    overlay = new CoverageOverlay(state.grid.cols, state.grid.rows);
    overlay.build(raw.cells);
    if (editor.selection && !selected.value) select(null);
    else syncForm();
    resize();
  }
);

watch(
  () => state.tick,
  () => {
    if (overlay) overlay.applyDelta(raw.delta);
    draw();
  }
);

watch(() => editor.showGrid, draw);
watch(isManual, (manual) => {
  if (!manual) releaseKeys();
});

onMounted(() => {
  window.addEventListener("resize", resize);
  window.addEventListener("keydown", onKeyDown);
  window.addEventListener("keyup", onKeyUp);
  window.addEventListener("blur", releaseKeys);
  resize();
});

onBeforeUnmount(() => {
  window.removeEventListener("resize", resize);
  window.removeEventListener("keydown", onKeyDown);
  window.removeEventListener("keyup", onKeyUp);
  window.removeEventListener("blur", releaseKeys);
});
</script>

<template>
  <div class="stage">
    <canvas
      ref="canvas"
      :style="{ cursor }"
      @mousedown="onPointerDown"
      @mousemove="onPointerMove"
      @mouseup="onPointerUp"
      @mouseleave="onPointerUp"
    />

    <p v-if="isManual" class="hint drive">
      <strong>W</strong> up, <strong>S</strong> down, <strong>A</strong> left,
      <strong>D</strong> right - two together drive the diagonal. The vacuum turns to
      face the way it is going. Arrow keys work too.
      <span v-if="!state.running">Press Start to power the vacuum.</span>
    </p>
    <p v-else-if="editor.tool === 'draw_room'" class="hint">
      Drag on the plan to draw a room. Overlap an existing room to create a doorway
      between them - rooms that do not touch get a reachability warning.
    </p>
    <p v-else-if="editor.tool === 'place_obstruction'" class="hint">
      Click to drop a {{ editor.obstruction.width }} &times; {{ editor.obstruction.height }} m
      {{ editor.obstruction.kind === "blocking" ? "blocking" : "pass-under" }} obstruction,
      or drag to size it.
    </p>
    <p v-else-if="editor.selection" class="hint">
      Drag to move, drag the blue corner to resize, Delete to remove.
    </p>

    <div class="legend">
      <span><i class="swatch uncleaned" />uncleaned</span>
      <span><i class="swatch partial" />partly cleaned</span>
      <span><i class="swatch cleaned" />cleaned</span>
      <span><i class="swatch blocking" />blocking</span>
      <span><i class="swatch passunder" />pass-under</span>
    </div>
  </div>
</template>

<style scoped>
.stage {
  flex: 1;
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

canvas {
  flex: 1;
  display: block;
  width: 100%;
  min-height: 0;
}

.hint {
  position: absolute;
  left: 14px;
  top: 12px;
  margin: 0;
  background: rgba(20, 24, 30, 0.9);
  border: 1px solid var(--accent);
  border-radius: 6px;
  padding: 6px 10px;
  font-size: 12px;
  max-width: min(520px, calc(100% - 28px));
}

.hint.drive {
  border-color: var(--good);
}

.hint.drive strong {
  display: inline-block;
  min-width: 16px;
  padding: 1px 5px;
  margin: 0 1px;
  border: 1px solid var(--edge);
  border-radius: 4px;
  background: var(--panel-2);
  text-align: center;
}

.legend {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  padding: 7px 14px;
  border-top: 1px solid var(--edge);
  font-size: 11px;
  color: var(--muted);
  flex: none;
}

.legend span {
  display: flex;
  align-items: center;
  gap: 5px;
}

.swatch {
  width: 11px;
  height: 11px;
  border-radius: 2px;
  display: inline-block;
}

.swatch.uncleaned {
  background: #6b5136;
}

.swatch.partial {
  background: #b98b3e;
}

.swatch.cleaned {
  background: #46c07a;
}

.swatch.blocking {
  background: #8d4a45;
}

.swatch.passunder {
  background: transparent;
  border: 1px dashed #9aa6b4;
}
</style>
