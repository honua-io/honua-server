#!/usr/bin/env python3
"""Boot the documented Production Compose install on fresh volumes; never print secrets.

The smoke runs the annotated blocks of "Production install from the shipped Compose
file" in docs/guides/deploy/docker-compose.md, verbatim, in a scratch directory that
holds copies of the shipped docker-compose.production.yml and .env.production.example,
exactly as a customer would. For isolation from other installs on the same host it
only renames the network and storage volume in the copied template and selects
unused loopback ports; it changes no documented command.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
GUIDE = ROOT / "docs/guides/deploy/docker-compose.md"
SECTION = "## Production install from the shipped Compose file"
BLOCK_PREFIX = "production."
# Known startup refusals, reported by marker only so no log text (or secret) is echoed.
DIAGNOSTICS = {
    "missing-keyring-certificate": "KeyRingCertificatePath' is required",
    "unreadable-keyring-certificate": "KeyRingCertificatePath' is set to",
    "invalid-limits": "Invalid limits configuration",
    "weak-admin-password": "Admin password must",
    "missing-master-key": "Set the connection encryption master key",
    "missing-keyring-password": "Set HONUA_KEYRING_PASSWORD",
    "missing-image": "Set an immutable HONUA_IMAGE",
    "missing-proxy-address": "Set the trusted proxy address",
    "missing-postgis-initialization": "PostGIS extension is not installed",
}
TEARDOWN_PLACEHOLDERS = ("HONUA_PROXY_IP", "HONUA_MASTER_KEY", "HONUA_KEYRING_PASSWORD", "POSTGRES_PASSWORD",
                         "HONUA_ADMIN_PASSWORD")


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def documented_blocks():
    """Return the runnable production blocks, in document order."""
    document = GUIDE.read_text(encoding="utf-8")
    start = document.find("\n" + SECTION + "\n")
    if start < 0:
        raise RuntimeError(f"{GUIDE.relative_to(ROOT)} has no '{SECTION}' section")
    end = document.find("\n## ", start + len(SECTION) + 2)
    section = document[start:end if end >= 0 else len(document)]
    spec = importlib.util.spec_from_file_location(
        "fences", ROOT / "scripts/docs-validation/extract-fenced-commands.py")
    fences = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fences)
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "section.md"
        path.write_text(section, encoding="utf-8")
        blocks = fences.parse(path)
    runnable = [(block_id, content) for block_id, mode, content in blocks
                if mode == "run" and block_id.startswith(BLOCK_PREFIX)]
    if not runnable:
        raise RuntimeError("The production section has no runnable docs-validation blocks")
    return runnable


def read_env(path):
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            values[key] = value
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--image", required=True, help="Exact published image@sha256 digest")
    parser.add_argument("--timeout", type=int, default=240, help="Seconds to wait for readiness")
    args = parser.parse_args()
    if not re.fullmatch(r"[^\s]+@sha256:[0-9a-f]{64}", args.image):
        parser.error("--image must be an immutable published digest")
    for tool in ("docker", "openssl", "bash", "sed"):
        if not shutil.which(tool):
            parser.error(f"{tool} is required")

    blocks = documented_blocks()
    project = "honua-production-smoke-" + secrets.token_hex(6)
    http_port, grpc_port = free_port(), free_port()
    environment = {key: value for key, value in os.environ.items()
                   if not key.startswith(("HONUA_", "ConnectionStrings__", "Security__", "Cors__", "COMPOSE_",
                                          "ForwardedHeaders__", "Authentication__", "Database__", "Operations__"))
                   and key != "POSTGRES_PASSWORD"}
    environment.update(HONUA_IMAGE=args.image, COMPOSE_PROJECT_NAME=project,
                       HONUA_HTTP_PORT=str(http_port), HONUA_GRPC_PORT=str(grpc_port))
    receipt = {"image": args.image, "fresh_volumes": True, "production": True,
               "documented_blocks": [block_id for block_id, _ in blocks], "ready": False}

    # Real disk next to the checkout is not required; the bind-mounted key ring only
    # needs a directory the Docker daemon can see.
    with tempfile.TemporaryDirectory(prefix=project + "-") as directory:
        install = Path(directory)
        shutil.copy2(ROOT / "docker-compose.production.yml", install / "docker-compose.production.yml")
        template = (ROOT / ".env.production.example").read_text(encoding="utf-8")
        template = re.sub(r"(?m)^HONUA_NETWORK_NAME=.*$", f"HONUA_NETWORK_NAME={project}", template)
        template = re.sub(r"(?m)^HONUA_STORAGE_VOLUME_NAME=.*$", f"HONUA_STORAGE_VOLUME_NAME={project}-storage",
                          template)
        (install / ".env.production.example").write_text(template, encoding="utf-8")

        def compose(*arguments, timeout=120):
            return subprocess.run(
                ["docker", "compose", "-f", "docker-compose.production.yml", "--env-file", ".env.production",
                 *arguments], cwd=install, env=environment, capture_output=True, text=True, timeout=timeout)

        def diagnose(text):
            return sorted(name for name, marker in DIAGNOSTICS.items() if marker in text)

        def status(path, *, host=None, authenticated=False, password=""):
            headers = {"X-Forwarded-Proto": "https"} if host else {}
            if host:
                headers["Host"] = host
            if authenticated:
                headers["X-API-Key"] = password
            request = urllib.request.Request(f"http://127.0.0.1:{http_port}{path}", headers=headers)
            try:
                with urllib.request.urlopen(request, timeout=5) as response:
                    return response.status
            except urllib.error.HTTPError as error:
                return error.code
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                return 0

        try:
            script = "\n".join(["set -euo pipefail"] + [
                f"printf '>>> docs-validation: %s\\n' {block_id}\n{content}" for block_id, content in blocks])
            result = subprocess.run(["bash", "-c", script], cwd=install, env=environment,
                                    capture_output=True, text=True, timeout=args.timeout + 900)
            completed = re.findall(r"^>>> docs-validation: (\S+)$", result.stdout, re.M)
            receipt["documented_exit_code"] = result.returncode
            if result.returncode:
                receipt["failed_block"] = completed[-1] if completed else None
                receipt["failure"] = "documented-install"
                logs = compose("logs", "--no-color", "--tail", "300", "honua").stdout if (
                    install / ".env.production").exists() else ""
                receipt["diagnostics"] = diagnose(result.stderr + logs)
            else:
                values = read_env(install / ".env.production")
                password = values.get("HONUA_ADMIN_PASSWORD", "")
                host = values.get("HONUA_HOST", "")
                deadline = time.monotonic() + args.timeout
                ready_status = 0
                while time.monotonic() < deadline:
                    ready_status = status("/healthz/ready")
                    if ready_status == 200:
                        break
                    time.sleep(2)
                receipt["ready_status"] = ready_status
                if ready_status != 200:
                    receipt["failure"] = "readiness"
                    receipt["diagnostics"] = diagnose(compose("logs", "--no-color", "--tail", "300", "honua").stdout)
                else:
                    receipt["live_status"] = status("/healthz/live")
                    receipt["anonymous_admin_status"] = status("/api/v1/admin/config", host=host)
                    receipt["proxied_admin_status"] = status(
                        "/api/v1/admin/config", host=host, authenticated=True, password=password)
                    receipt["untrusted_host_status"] = status(
                        "/api/v1/admin/config", host="untrusted.invalid", authenticated=True, password=password)
                    receipt["ready"] = (receipt["live_status"] == 200
                                        and receipt["anonymous_admin_status"] == 401
                                        and receipt["proxied_admin_status"] == 200
                                        and receipt["untrusted_host_status"] == 400)
                    if not receipt["ready"]:
                        receipt["failure"] = "authentication-or-hostname"
        finally:
            down = subprocess.run(
                ["docker", "compose", "-f", "docker-compose.production.yml", "--env-file",
                 ".env.production" if (install / ".env.production").exists() else ".env.production.example",
                 "down", "--volumes", "--remove-orphans"],
                # Placeholders only satisfy interpolation for a stack that never started;
                # project, network and volume names still come from the env file.
                cwd=install, env={**{key: "teardown" for key in TEARDOWN_PLACEHOLDERS}, **environment},
                capture_output=True, text=True, timeout=300)
            receipt["cleanup_succeeded"] = down.returncode == 0
    print(json.dumps(receipt, sort_keys=True))
    return 0 if receipt["ready"] and receipt["cleanup_succeeded"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
