"""WebSocket wrapper around the simulation engine.

Runs the fixed-timestep loop in real time, broadcasts state after each tick,
and applies JSON commands from any connected client.

Protocol
--------
server -> client
    {"type": "world", ...}   house, obstructions, grid, algorithms, coverings,
                             limits and a full coverage snapshot. Sent on
                             connect and whenever the layout changes.
    {"type": "state", ...}   every tick: pose, battery, coverage stats and
                             warnings, plus `coverageDelta` - a flat
                             [cellIndex, percent, ...] list of cells that
                             changed since the previous frame.
    {"type": "error", "message": ...}

client -> server
    {"command": "start"}                    {"command": "stop"}
    {"command": "reset"}                    {"command": "recharge"}
    {"command": "set_speed", "speed": 0.8, "unit": "m/s" | "ft/s"}
    {"command": "set_battery_capacity", "minutes": 45}
    {"command": "set_algorithm", "algorithm": "spiral"}
    {"command": "set_floor_covering", "covering": "low_pile"}   house default
    {"command": "set_room_covering", "room": "kitchen", "covering": "tile" | null}
    {"command": "set_efficiency", "efficiency": 0.7, "covering": "tile"}
    {"command": "place_obstruction", "x": 3, "y": 2,
     "width": 0.8, "height": 0.6, "kind": "blocking" | "pass_under"}
    {"command": "update_obstruction", "id": 3, "x": 3, "y": 2, ...}
    {"command": "remove_obstruction", "id": 3}
    {"command": "set_algorithm_params", "params": {"lane_spacing": 0.4}}
    {"command": "set_physics", "params": {"maxAccel": 1.2, "maxTurnRate": 3.0,
                                          "bumperDamping": 0.5, "wheelBase": 0.23}}
    {"command": "set_drive", "x": 1, "y": -1}   manual: a direction on the plan
    {"command": "add_room", "name": "porch", "x": 12, "y": 3,
     "width": 3, "height": 2.4}
    {"command": "update_room", "name": "porch", "x": 12, "y": 4,
     "width": 2, "height": 2, "newName": "deck"}
    {"command": "remove_room", "name": "porch"}
"""

import argparse
import asyncio
import json
import logging

from websockets.asyncio.server import serve

from engine import Simulation

HOST = "127.0.0.1"
PORT = 8765
MAX_STEPS_PER_FRAME = 20  # keeps a stalled loop from spiralling

log = logging.getLogger("vacuum.server")


class SimulationServer:
    def __init__(self, host: str = HOST, port: int = PORT, time_scale: float = 1.0):
        self.host = host
        self.port = port
        self.time_scale = time_scale
        self.simulation = Simulation()
        self.clients: set = set()

    # -- messages -----------------------------------------------------------
    def world_message(self) -> str:
        """Full resync, including a complete coverage snapshot."""
        return json.dumps({"type": "world", **self.simulation.describe()})

    def state_message(self) -> str:
        state = self.simulation.get_state()
        state["coverageDelta"] = self.simulation.world.grid.drain_delta()
        return json.dumps({"type": "state", "timeScale": self.time_scale, **state})

    async def broadcast(self, message: str) -> None:
        for websocket in list(self.clients):
            try:
                await websocket.send(message)
            except Exception:
                self.clients.discard(websocket)

    # -- client plumbing ----------------------------------------------------
    async def handler(self, websocket) -> None:
        self.clients.add(websocket)
        log.info("client connected (%d total)", len(self.clients))
        try:
            await websocket.send(self.world_message())
            await websocket.send(self.state_message())
            async for raw in websocket:
                await self._handle_command(websocket, raw)
        except Exception as exc:  # a dropped client must not kill the loop
            log.debug("client error: %s", exc)
        finally:
            self.clients.discard(websocket)
            log.info("client disconnected (%d left)", len(self.clients))

    async def _handle_command(self, websocket, raw: str) -> None:
        try:
            message = json.loads(raw)
        except json.JSONDecodeError:
            await websocket.send(json.dumps({"type": "error", "message": "bad JSON"}))
            return

        command = message.get("command")
        sim = self.simulation
        resync = False

        try:
            if command == "start":
                sim.start()
            elif command == "stop":
                sim.stop()
            elif command == "reset":
                sim.reset()
                resync = True
            elif command == "recharge":
                sim.recharge()
            elif command == "set_speed":
                sim.set_speed(message.get("speed", sim.vacuum.speed),
                              message.get("unit", "m/s"))
            elif command == "set_battery_capacity":
                sim.set_battery_capacity(message.get("minutes", 90))
            elif command == "set_algorithm":
                sim.set_algorithm(message.get("algorithm", sim.algorithm_name))
            elif command == "set_floor_covering":
                sim.set_floor_covering(message.get("covering", sim.covering.name))
            elif command == "set_efficiency":
                sim.set_efficiency(
                    message.get("efficiency", sim.efficiency), message.get("covering")
                )
            elif command == "set_room_covering":
                sim.set_room_covering(message.get("room", ""), message.get("covering"))
                resync = True
            elif command == "place_obstruction":
                sim.place_obstruction(
                    message.get("x", 0.0),
                    message.get("y", 0.0),
                    message.get("width", 0.8),
                    message.get("height", 0.6),
                    message.get("kind", "blocking"),
                )
                resync = True
            elif command == "update_obstruction":
                sim.update_obstruction(
                    message.get("id"),
                    x=message.get("x"), y=message.get("y"),
                    width=message.get("width"), height=message.get("height"),
                    kind=message.get("kind"),
                )
                resync = True
            elif command == "remove_obstruction":
                sim.remove_obstruction(message.get("id"))
                resync = True
            elif command == "set_drive":
                sim.set_drive(message.get("x", 0.0), message.get("y", 0.0))
            elif command == "set_physics":
                sim.set_physics(message.get("params", {}))
            elif command == "set_algorithm_params":
                sim.set_algorithm_params(message.get("params", {}))
            elif command == "add_room":
                sim.add_room(
                    message.get("name", "room"),
                    message.get("x", 0.0), message.get("y", 0.0),
                    message.get("width", 3.0), message.get("height", 3.0),
                    message.get("covering"),
                )
                resync = True
            elif command == "update_room":
                sim.update_room(
                    message.get("name", ""),
                    x=message.get("x"), y=message.get("y"),
                    width=message.get("width"), height=message.get("height"),
                    new_name=message.get("newName"),
                )
                resync = True
            elif command == "remove_room":
                sim.remove_room(message.get("name", ""))
                resync = True
            elif command == "set_time_scale":
                self.time_scale = max(0.1, min(50.0, float(message.get("scale", 1.0))))
            else:
                await websocket.send(
                    json.dumps({"type": "error",
                                "message": f"unknown command {command!r}"})
                )
                return
        except (ValueError, TypeError) as exc:
            await websocket.send(json.dumps({"type": "error", "message": str(exc)}))
            return

        if resync:
            await self.broadcast(self.world_message())
        await self.broadcast(self.state_message())

    # -- the real-time loop --------------------------------------------------
    async def run_loop(self) -> None:
        loop = asyncio.get_running_loop()
        dt = self.simulation.dt
        previous = loop.time()
        backlog = 0.0

        while True:
            await asyncio.sleep(dt)
            now = loop.time()
            elapsed = now - previous
            previous = now
            if not self.simulation.running:
                # Nothing is moving: no ticks, no frames. State still goes out
                # on every command, so clients stay in sync while paused.
                backlog = 0.0
                continue
            backlog += elapsed * self.time_scale

            steps = 0
            while backlog >= dt and steps < MAX_STEPS_PER_FRAME:
                self.simulation.step()
                backlog -= dt
                steps += 1
            if steps == MAX_STEPS_PER_FRAME:
                backlog = 0.0  # we are behind; drop the debt rather than chase it

            if steps and self.clients:
                await self.broadcast(self.state_message())

    async def serve_forever(self) -> None:
        async with serve(self.handler, self.host, self.port):
            log.info("listening on ws://%s:%d", self.host, self.port)
            print(f"READY ws://{self.host}:{self.port}", flush=True)
            await self.run_loop()


def main() -> None:
    parser = argparse.ArgumentParser(description="Vacuum simulation WebSocket server")
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--time-scale", type=float, default=1.0)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="[server] %(message)s")
    server = SimulationServer(args.host, args.port, args.time_scale)
    try:
        asyncio.run(server.serve_forever())
    except KeyboardInterrupt:
        log.info("shutting down")


if __name__ == "__main__":
    main()
