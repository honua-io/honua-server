"""Exercise the canary script's probes with the server image's available tools."""
import os
from pathlib import Path
import socketserver
import subprocess
import threading

import pytest

ROOT = Path(__file__).resolve().parents[3]


@pytest.mark.parametrize("status", [
    "200 OK", "204 No Content", "301 Moved", "503 Unavailable",
    "503 200 unavailable", "2000 Invalid", None,
])
def test_canary_probes_work_without_http_clients(tmp_path, status):
    requests = []

    class Handler(socketserver.BaseRequestHandler):
        def handle(self):
            # Read through the header terminator: TCP may deliver the request in chunks,
            # and closing with unread bytes can reset the connection.
            data = b""
            while b"\r\n\r\n" not in data:
                chunk = self.request.recv(4096)
                if not chunk:
                    break
                data += chunk
            requests.append(data)
            if status is not None:
                self.request.sendall(f"HTTP/1.0 {status}\r\nContent-Length: 0\r\n\r\n".encode())

    with socketserver.TCPServer(("127.0.0.1", 0), Handler) as server:
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        # Execute only function definitions; never run deployment mutations.
        script = (ROOT / "scripts/cloud/deploy-canary.sh").read_text().split("# Main deployment logic", 1)[0]
        harness = r'''
# Model a server pod with bash but no HTTP client. Redirect its loopback port
# to the test HTTP server and execute the real command supplied to kubectl.
kubectl() {
    while [[ "$1" != -- ]]; do shift; done
    shift
    [[ "$1" == bash && "$2" == -c ]] || return 127
    command bash -c "${3//\/8080/\/$TEST_PORT}"
}
date() {
    local count=0
    [[ ! -f "$TEST_CLOCK" ]] || read -r count < "$TEST_CLOCK"
    echo $((count + 1)) > "$TEST_CLOCK"
    if ((count < 2)); then echo 0; else echo 2; fi
}
sleep() { :; }
monitor_canary_metrics
'''
        result = subprocess.run(
            ["bash", "-c", script + harness, "probe-test", "test-image"],
            env=os.environ | {"TEST_PORT": str(server.server_address[1]),
                              "TEST_CLOCK": str(tmp_path / "clock"), "PROMOTION_DELAY": "1"},
            capture_output=True, text=True, timeout=10,
        )
        server.shutdown()
        worker.join()

    assert result.returncode == 0, result.stderr
    expected = "200" if status == "200 OK" else "000"
    assert f"Canary metrics status: {expected}" in result.stdout
    assert f"Stable metrics status: {expected}" in result.stdout
    assert len(requests) == 2
    assert all(request.startswith(b"GET /api/metrics/health HTTP/1.0\r\n") for request in requests)
