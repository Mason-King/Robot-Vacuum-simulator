"""Desktop launcher: one native window running the whole simulator.

Starts the WebSocket server as a subprocess, serves /client over a local HTTP
server, and points a pywebview window at it. Closing the window shuts both
down. For development you can skip this and run the two halves by hand:

    python -m server
    cd client && npm run dev     # Vite dev server with hot reload
"""

import socket
import subprocess
import sys
import threading
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlencode

import webview

ROOT = Path(__file__).parent.resolve()
CLIENT_DIR = ROOT / "client"
CLIENT_DIST = CLIENT_DIR / "dist"  # `npm run build` output
WINDOW_SIZE = (1180, 760)
SERVER_START_TIMEOUT = 15.0


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, *args):  # keep the console for engine output only
        pass


def serve_client(port: int) -> ThreadingHTTPServer:
    handler = partial(QuietHandler, directory=str(CLIENT_DIST))
    httpd = ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


def start_engine(port: int) -> subprocess.Popen:
    """Launch the WebSocket server and wait for its READY line."""
    process = subprocess.Popen(
        [sys.executable, "-u", "-m", "server", "--port", str(port)],
        cwd=str(ROOT),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )

    ready = threading.Event()

    def pump():
        for line in process.stdout:
            print(line.rstrip(), flush=True)
            if line.startswith("READY"):
                ready.set()
        ready.set()  # the process died; stop waiting

    threading.Thread(target=pump, daemon=True).start()
    if not ready.wait(SERVER_START_TIMEOUT):
        process.terminate()
        raise RuntimeError("simulation server did not start in time")
    if process.poll() is not None:
        raise RuntimeError("simulation server exited during startup")
    return process


def main() -> int:
    if not (CLIENT_DIST / "index.html").exists():
        print("The client has not been built. Run:\n"
              "    cd client && npm install && npm run build")
        return 1

    ws_port, http_port = free_port(), free_port()
    engine = start_engine(ws_port)
    httpd = serve_client(http_port)

    query = urlencode({"ws": f"ws://127.0.0.1:{ws_port}"})
    webview.create_window(
        "Robot Vacuum Simulator",
        f"http://127.0.0.1:{http_port}/index.html?{query}",
        width=WINDOW_SIZE[0],
        height=WINDOW_SIZE[1],
    )
    try:
        webview.start()
    finally:
        httpd.shutdown()
        engine.terminate()
        try:
            engine.wait(timeout=5)
        except subprocess.TimeoutExpired:
            engine.kill()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
