#!/usr/bin/env python3
"""Boot the documented Production Compose on fresh volumes; never print secrets."""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[2]


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True, help="Exact published image@sha256 digest")
    parser.add_argument("--initialize-postgis", action="store_true",
                        help="Apply the documented troubleshooting remedy to isolate the mapper regression")
    parser.add_argument("--timeout", type=int, default=180)
    args = parser.parse_args()
    if not re.fullmatch(r"[^\s]+@sha256:[0-9a-f]{64}", args.image):
        parser.error("--image must be an immutable published digest")
    project = "honua-production-smoke-" + secrets.token_hex(6)
    port = free_port()
    document = (ROOT / "docs/guides/deploy/docker-compose.md").read_text()
    match = re.search(r"cat > docker-compose.yml <<'EOF'\n(.*?)\nEOF", document, re.S)
    if not match:
        raise RuntimeError("Documented Production Compose block was not found")
    compose_text = match.group(1).replace("127.0.0.1:8080:8080", f"127.0.0.1:{port}:8080")
    compose_text = compose_text.replace("127.0.0.1:8081:8081", f"127.0.0.1:{free_port()}:8081")
    environment = dict(os.environ)
    environment.update(HONUA_IMAGE=args.image, POSTGRES_PASSWORD=secrets.token_hex(32),
                       HONUA_ADMIN_PASSWORD="Aa1!" + secrets.token_hex(32),
                       HONUA_MASTER_KEY=secrets.token_hex(32), HONUA_CORS_ORIGIN="https://app.example.com",
                       HONUA_HOST="honua.example.com", HONUA_STORAGE_VOLUME_NAME=project + "-storage")
    receipt = {"image": args.image, "fresh_volumes": True, "production": True,
               "postgis_remedy": args.initialize_postgis, "ready": False}
    with tempfile.TemporaryDirectory(prefix=project) as directory:
        compose_file = Path(directory) / "compose.yml"
        compose_file.write_text(compose_text)
        command = ["docker", "compose", "--project-name", project, "-f", str(compose_file)]

        def compose(*arguments):
            return subprocess.run(command + list(arguments), env=environment,
                                  capture_output=True, text=True, timeout=args.timeout + 60)

        def status(path):
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=3) as response:
                    return response.status
            except urllib.error.HTTPError as error:
                return error.code
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                return 0

        try:
            if compose("up", "-d", "--wait", "--wait-timeout", str(args.timeout), "postgres", "redis").returncode:
                receipt["failure"] = "backing-services-startup"
            else:
                if args.initialize_postgis:
                    result = compose("exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1",
                                     "-U", "honua", "-d", "honua", "-c", "CREATE EXTENSION IF NOT EXISTS postgis;")
                    if result.returncode:
                        raise RuntimeError("PostGIS troubleshooting remedy failed")
                if compose("up", "-d", "--no-deps", "honua").returncode:
                    receipt["failure"] = "server-container-startup"
                else:
                    deadline = time.monotonic() + args.timeout
                    while time.monotonic() < deadline:
                        if status("/healthz/ready") == 200:
                            receipt["live_status"] = status("/healthz/live")
                            receipt["anonymous_admin_status"] = status("/api/v1/admin/config")
                            receipt["ready"] = (receipt["live_status"] == 200
                                                and receipt["anonymous_admin_status"] == 401)
                            break
                        logs = compose("logs", "--no-color", "--tail", "100", "honua").stdout
                        if "exactly one safe approval mapper" in logs:
                            receipt["failure"] = "missing-safe-rollback-approval-mappers"
                            break
                        if "PostGIS extension is not installed" in logs:
                            receipt["failure"] = "missing-postgis-initialization"
                            break
                        time.sleep(2)
                    if not receipt["ready"] and "failure" not in receipt:
                        receipt["failure"] = "readiness-or-authentication"
        finally:
            receipt["cleanup_succeeded"] = compose("down", "--volumes", "--remove-orphans").returncode == 0
    print(json.dumps(receipt, sort_keys=True))
    return 0 if receipt["ready"] and receipt["cleanup_succeeded"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
