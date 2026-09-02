/* Connection to the Python engine, and the reactive mirror of its state.

   A module-level store rather than a Pinia one: there is a single simulation
   per window, and every component wants the same instance. Swapping this for
   `defineStore` later would not change any component. */

import { computed, reactive } from "vue";

const M_PER_FT = 0.3048;
const RECONNECT_MS = 1000;

const params = new URLSearchParams(window.location.search);
const WS_URL = params.get("ws") || "ws://127.0.0.1:8765";

/** Per-cell coverage is thousands of numbers a tick - far too much churn for a
    reactive proxy, so it lives outside the reactive state. */
const raw = { cells: [], delta: [] };

const state = reactive({
  connected: false,
  // world (static until the layout changes)
  house: null,
  obstructions: [],
  grid: null,
  algorithms: [],
  coverings: [],
  limits: {
    speedMs: [0.05, 2],
    capacityMinutes: [1, 480],
    efficiency: [0.05, 1],
    maxAccel: [0.1, 4],
    maxTurnRate: [0.3, 8],
  },
  worldVersion: 0,
  // live state
  tick: 0,
  simTime: 0,
  running: false,
  status: "connecting",
  notice: null,
  vacuum: null,
  battery: { level: 100, capacityMinutes: 90, empty: false },
  coverage: {
    percent: 0,
    cleanedPercent: 0,
    meanCleanliness: 0,
    cleanableArea: 0,
    nonCleanableArea: 0,
  },
  warnings: [],
  finalReport: null,
  algorithm: "snaking",
  algorithmParams: {},
  pendingSpeed: null,
  floor: { covering: "hardwood", efficiency: 0.95 },
  timeScale: 1,
});

let socket = null;

function send(command, payload = {}) {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ command, ...payload }));
  }
}

function applyWorld(message) {
  state.house = message.house;
  state.obstructions = message.obstructions;
  state.grid = message.grid;
  state.algorithms = message.algorithms;
  state.coverings = message.coverings;
  state.limits = message.limits;
  raw.cells = message.coverageCells;
  state.worldVersion += 1;
}

function applyState(message) {
  state.tick = message.tick;
  state.simTime = message.simTime;
  state.running = message.running;
  state.status = message.status;
  state.notice = message.notice;
  state.vacuum = message.vacuum;
  state.battery = message.battery;
  state.coverage = message.coverage;
  state.warnings = message.warnings;
  state.finalReport = message.finalReport;
  state.algorithm = message.algorithm;
  state.algorithmParams = message.algorithmParams || {};
  state.pendingSpeed = message.pendingSpeed;
  state.floor = message.floor;
  if (message.timeScale) state.timeScale = message.timeScale;

  raw.delta = message.coverageDelta || [];
  for (let i = 0; i < raw.delta.length; i += 2) {
    raw.cells[raw.delta[i]] = raw.delta[i + 1];
  }
}

/** Route one server message into the store. Exported so tests can drive the
    UI from recorded frames without a socket. */
function handleMessage(message) {
  if (message.type === "world") applyWorld(message);
  else if (message.type === "state") applyState(message);
  else if (message.type === "error") console.warn("engine:", message.message);
}

function connect() {
  socket = new WebSocket(WS_URL);

  socket.onopen = () => {
    state.connected = true;
  };
  socket.onmessage = (event) => handleMessage(JSON.parse(event.data));
  socket.onclose = () => {
    state.connected = false;
    state.status = "disconnected";
    setTimeout(connect, RECONNECT_MS); // the server may still be starting
  };
  socket.onerror = () => socket.close();
}

function disconnect() {
  if (socket) socket.close();
  socket = null;
}

// -- derived -----------------------------------------------------------------
const speedMs = computed(() => (state.vacuum ? state.vacuum.speedMs : 0.35));
const speedFts = computed(() => speedMs.value / M_PER_FT);
const isManual = computed(() => state.algorithm === "manual");
const activeAlgorithm = computed(
  () => state.algorithms.find((a) => a.name === state.algorithm) || null
);
const paramSchema = computed(() =>
  activeAlgorithm.value ? activeAlgorithm.value.params : []
);
const clock = computed(() => {
  const total = Math.floor(state.simTime);
  const mm = String(Math.floor(total / 60)).padStart(2, "0");
  const ss = String(total % 60).padStart(2, "0");
  return `${mm}:${ss}`;
});
/** Warnings must be acknowledged again whenever they actually change. */
/** The covering a room actually has: its own, or the house default. */
function coveringFor(room) {
  const name = (room && room.covering) || state.floor.covering;
  return state.coverings.find((c) => c.name === name) || null;
}

const warningSignature = computed(() =>
  state.warnings.map((w) => `${w.code}:${w.room}`).join("|")
);

// -- commands ----------------------------------------------------------------
const actions = {
  start: () => send("start"),
  stop: () => send("stop"),
  reset: () => send("reset"),
  recharge: () => send("recharge"),
  setSpeed: (speed, unit) => send("set_speed", { speed: Number(speed), unit }),
  setBatteryCapacity: (minutes) =>
    send("set_battery_capacity", { minutes: Number(minutes) }),
  setAlgorithm: (algorithm) => send("set_algorithm", { algorithm }),
  setAlgorithmParams: (params) => send("set_algorithm_params", { params }),
  setPhysics: (params) => send("set_physics", { params }),
  setFloorCovering: (covering) => send("set_floor_covering", { covering }),
  setEfficiency: (efficiency, covering) =>
    send("set_efficiency", { efficiency: Number(efficiency), covering }),
  setRoomCovering: (room, covering) =>
    send("set_room_covering", { room, covering }),
  setTimeScale: (scale) => send("set_time_scale", { scale: Number(scale) }),
  setDrive: (x, y) => send("set_drive", { x, y }),
  placeObstruction: (box) => send("place_obstruction", box),
  updateObstruction: (changes) => send("update_obstruction", changes),
  removeObstruction: (id) => send("remove_obstruction", { id }),
  addRoom: (room) => send("add_room", room),
  updateRoom: (changes) => send("update_room", changes),
  removeRoom: (name) => send("remove_room", { name }),
};

export const M_PER_FOOT = M_PER_FT;

export function useSimulation() {
  return {
    state,
    raw,
    connect,
    disconnect,
    handleMessage,
    speedMs,
    speedFts,
    isManual,
    activeAlgorithm,
    paramSchema,
    clock,
    warningSignature,
    coveringFor,
    ...actions,
  };
}
