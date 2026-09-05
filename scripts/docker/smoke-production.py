#!/usr/bin/env python3
"""Boot the documented Production Compose on fresh volumes; never print secrets."""
import argparse
import base64
import ipaddress
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
    artifact = parser.add_mutually_exclusive_group(required=True)
    artifact.add_argument("--image", help="Exact published image@sha256 digest")
    artifact.add_argument("--runtime", type=Path,
                          help="Local Honua.Server.dll for source validation; never a published-image receipt")
    parser.add_argument("--initialize-postgis", action="store_true",
                        help="Apply the documented troubleshooting remedy to isolate the mapper regression")
    parser.add_argument("--timeout", type=int, default=180)
    args = parser.parse_args()
    if args.image and not re.fullmatch(r"[^\s]+@sha256:[0-9a-f]{64}", args.image):
        parser.error("--image must be an immutable published digest")
    if args.runtime and (not args.runtime.is_file() or args.runtime.name != "Honua.Server.dll"):
        parser.error("--runtime must name a built Honua.Server.dll")
    project = "honua-production-smoke-" + secrets.token_hex(6)
    port = free_port()
    proxy_port = free_port()
    document = (ROOT / "docs/guides/deploy/docker-compose.md").read_text()
    match = re.search(r"cat > docker-compose.yml <<'EOF'\n(.*?)\nEOF", document, re.S)
    if not match:
        raise RuntimeError("Documented Production Compose block was not found")
    compose_text = match.group(1).replace("127.0.0.1:8080:8080", f"127.0.0.1:{port}:8080")
    compose_text = compose_text.replace("127.0.0.1:8081:8081", f"127.0.0.1:{free_port()}:8081")
    environment = {key: value for key, value in os.environ.items()
                   if not key.startswith(("HONUA_", "ConnectionStrings__", "Security__", "Cors__",
                                          "ForwardedHeaders__", "Authentication__", "Database__"))}
    values = dict(HONUA_IMAGE=args.image or "mcr.microsoft.com/dotnet/aspnet:10.0", POSTGRES_PASSWORD=secrets.token_hex(32),
                       HONUA_ADMIN_PASSWORD="Aa1!" + secrets.token_hex(32),
                       HONUA_MASTER_KEY=secrets.token_hex(32), HONUA_CORS_ORIGIN="https://app.example.com",
                       HONUA_HOST="honua.example.com", HONUA_STORAGE_VOLUME_NAME=project + "-storage",
                       HONUA_NETWORK_NAME=project, HONUA_PROXY_IP="127.0.0.1")
    environment.update(values)
    receipt = {"image": args.image, "published_image": args.image is not None,
               "fresh_volumes": True, "production": True,
               "postgis_remedy": args.initialize_postgis, "ready": False}
    with tempfile.TemporaryDirectory(prefix=project) as directory:
        compose_file = Path(directory) / "compose.yml"
        compose_file.write_text(compose_text)
        env_file = Path(directory) / ".env.production"
        env_file.write_text("\n".join(f"{key}={value}" for key, value in values.items()))
        env_file.chmod(0o600)
        proxy_file = Path(directory) / "proxy.json"
        proxy_file.write_text(json.dumps({
            "services": {"honua": {"environment": {"Authentication__BasicCompatibility__Enabled": "true"}},
                         "smoke_proxy": {
                "image": "caddy:2-alpine",
                "ports": [f"127.0.0.1:{proxy_port}:8080"],
                "configs": [{"source": "smoke_caddy", "target": "/etc/caddy/Caddyfile"}],
            }},
            # Model the scheme asserted by the documented TLS terminator; real
            # certificate/DNS qualification remains on the deployment host.
            "configs": {"smoke_caddy": {"content": ":8080 {\n  reverse_proxy honua:8080 {\n    header_up X-Forwarded-Proto https\n  }\n}\n"}},
        }))
        for filename, sql in re.findall(r"cat > ([\w-]+\.sql) <<'EOF'\n(.*?)\nEOF", document, re.S):
            (Path(directory) / filename).write_text(sql + "\n")
        command = ["docker", "compose", "--project-name", project,
                   "-f", str(compose_file), "-f", str(proxy_file)]
        if args.runtime:
            runtime_file = Path(directory) / "runtime.json"
            runtime_file.write_text(json.dumps({"services": {"honua": {
                "entrypoint": ["dotnet", "/app/Honua.Server.dll"],
                "working_dir": "/app",
                "volumes": [{"type": "bind", "source": str(args.runtime.resolve().parent),
                             "target": "/app", "read_only": True}],
            }}}))
            command += ["-f", str(runtime_file)]

        def compose(*arguments):
            return subprocess.run(command + list(arguments), env=environment,
                                  capture_output=True, text=True, timeout=args.timeout + 60)

        def status(path, *, proxy=False, authenticated=False, host=None, basic=False, spoof_https=False):
            headers = {}
            if host:
                headers["Host"] = host
            if authenticated:
                headers["X-API-Key"] = environment["HONUA_ADMIN_PASSWORD"]
            if basic:
                credential = base64.b64encode(("admin:" + environment["HONUA_ADMIN_PASSWORD"]).encode()).decode()
                headers["Authorization"] = "Basic " + credential
            if spoof_https:
                headers["X-Forwarded-Proto"] = "https"
            request = urllib.request.Request(
                f"http://127.0.0.1:{proxy_port if proxy else port}{path}", headers=headers)
            try:
                with urllib.request.urlopen(request, timeout=3) as response:
                    return response.status
            except urllib.error.HTTPError as error:
                return error.code
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                return 0

        try:
            if compose("up", "-d", "--wait", "--wait-timeout", str(args.timeout), "postgres", "redis").returncode:
                receipt["failure"] = "backing-services-startup"
            else:
                if compose("up", "-d", "--no-deps", "smoke_proxy").returncode:
                    raise RuntimeError("The isolated proxy could not start")
                proxy_id = compose("ps", "-q", "smoke_proxy").stdout.strip()
                proxy_address = subprocess.run(
                    ["docker", "inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", proxy_id],
                    capture_output=True, text=True, check=True).stdout.strip()
                environment["HONUA_PROXY_IP"] = str(ipaddress.ip_address(proxy_address))
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
                    if receipt["ready"]:
                        deadline = time.monotonic() + 30
                        while time.monotonic() < deadline:
                            if status("/healthz/ready", proxy=True) == 200:
                                break
                            time.sleep(1)
                        receipt["proxy_authenticated_admin_status"] = status(
                            "/api/v1/admin/config", proxy=True, authenticated=True, host=environment["HONUA_HOST"])
                        receipt["proxy_untrusted_host_status"] = status(
                            "/api/v1/admin/config", proxy=True, authenticated=True, host="untrusted.invalid")
                        receipt["proxy_https_basic_status"] = status(
                            "/api/v1/admin/config", proxy=True, basic=True, host=environment["HONUA_HOST"])
                        receipt["untrusted_forwarded_https_status"] = status(
                            "/api/v1/admin/config", basic=True, spoof_https=True, host=environment["HONUA_HOST"])
                        receipt["ready"] = (receipt["proxy_authenticated_admin_status"] == 200
                                            and receipt["proxy_untrusted_host_status"] == 400
                                            and receipt["proxy_https_basic_status"] == 200
                                            and receipt["untrusted_forwarded_https_status"] == 401)
                        if not receipt["ready"]:
                            receipt["failure"] = "proxy-hostname-or-authentication"
        finally:
            receipt["cleanup_succeeded"] = compose("down", "--volumes", "--remove-orphans").returncode == 0
    print(json.dumps(receipt, sort_keys=True))
    return 0 if receipt["ready"] and receipt["cleanup_succeeded"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
