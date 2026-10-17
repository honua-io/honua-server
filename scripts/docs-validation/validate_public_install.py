#!/usr/bin/env python3
"""Replay the published Docker quickstart, anonymously, without a server build."""
from __future__ import annotations

import importlib.util
import json
import os
import re
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from install_artifacts import ROOT, MANIFEST, check

spec = importlib.util.spec_from_file_location(
    "fences", Path(__file__).with_name("extract-fenced-commands.py"))
fences = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fences)


def public_blocks() -> dict[str, str]:
    blocks = fences.parse(ROOT / "docs/get-started/quickstart.md",
                          languages=("bash", "sh", "yaml", "python", "powershell"))
    runnable = {name.removeprefix("install."): body
                for name, mode, body in blocks if mode == "run"}
    expected = {"compose", "secrets", "keyring", "start", "seed", "clients", "publish", "query"}
    if runnable.keys() != expected:
        raise ValueError(f"Review the public quickstart replay steps: {sorted(runnable)}")
    return runnable


def free_ports() -> tuple[int, int]:
    # Hold both sockets together so the OS cannot select the same port twice.
    with socket.socket() as http, socket.socket() as grpc:
        http.bind(("127.0.0.1", 0))
        grpc.bind(("127.0.0.1", 0))
        return http.getsockname()[1], grpc.getsockname()[1]


def wait_ready(base: str, timeout: int = 180) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(base + "/healthz/ready", timeout=5) as response:
                if response.status == 200 and response.read().decode().strip() == "Ready":
                    return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(2)
    raise RuntimeError("Public image did not become ready")


def verify_features(features: list[dict]) -> None:
    expected = {(1, "west", -157.875, 21.3125), (2, "east", -155.0625, 19.6875)}
    observed = {(row["attributes"]["id"], row["attributes"]["name"],
                 row["geometry"]["x"], row["geometry"]["y"]) for row in features}
    if len(features) != 2 or observed != expected:
        raise ValueError("Published sample identities, names or coordinates differ from the quickstart")


def main() -> int:
    problems = check()
    if problems:
        raise ValueError("\n".join(problems))
    blocks = public_blocks()
    image = json.loads((ROOT / MANIFEST).read_text())["serverImage"]
    for command in ("docker", "python", "openssl", "bash"):
        if not shutil.which(command):
            raise RuntimeError(f"Missing quickstart prerequisite: {command}")
    artifacts = ROOT / "artifacts/docs-validation/public-install"
    artifacts.mkdir(parents=True, exist_ok=True)
    receipt: dict = {"image": image, "passed": False}
    project = "honua-docs-" + uuid.uuid4().hex[:12]
    receipt["project"] = project
    with tempfile.TemporaryDirectory(prefix="public-install-", dir=artifacts) as directory:
        work = Path(directory)
        env = os.environ.copy()
        # Compose host configuration must not select an unrelated stack or image.
        for key in tuple(env):
            if key.startswith(("COMPOSE_", "HONUA_")) or key == "POSTGRES_PASSWORD":
                del env[key]
        env.update(COMPOSE_PROJECT_NAME=project, COMPOSE_FILE=str(work / "compose.yaml"))
        env_file: dict[str, str] = {}

        def redact(text: str) -> str:
            for key, value in env_file.items():
                if ("PASSWORD" in key or "KEY" in key) and value:
                    text = text.replace(value, "[REDACTED]")
            return text

        def run(command: list[str], timeout: int = 300) -> str:
            result = subprocess.run(command, cwd=work, env=env, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
            if result.returncode:
                raise RuntimeError(redact(result.stdout + result.stderr))
            return result.stdout

        def shell(name: str, timeout: int = 300) -> str:
            print(f"Running documented block: {name}", flush=True)
            return run(["bash", "-euo", "pipefail", "-c", blocks[name]], timeout)

        compose = ["docker", "compose"]
        try:
            run(["docker", "info"])
            run(compose + ["version"])
            context = json.loads(run(["docker", "context", "inspect"]))[0]
            endpoint = context["Endpoints"]["docker"]["Host"]
            if not endpoint.startswith("unix://") or not env.get("DOCKER_HOST", endpoint).startswith("unix://"):
                raise RuntimeError("This local Linux proof requires a local Unix-socket Docker daemon")
            if not env.get("DOCKER_CONTEXT"):
                endpoint = env.get("DOCKER_HOST", endpoint)
            env.pop("DOCKER_CONTEXT", None)
            env["DOCKER_HOST"] = endpoint
            # An empty config proves public access even on a logged-in workstation.
            anonymous = work / "anonymous-docker"
            anonymous.mkdir()
            run(["docker", "--config", str(anonymous), "pull", "--platform", "linux/amd64", image], 600)
            receipt["anonymousPull"] = True
            receipt["repoDigests"] = json.loads(run(
                ["docker", "image", "inspect", image, "--format", "{{json .RepoDigests}}"]))
            (work / "compose.yaml").write_text(blocks["compose"])
            shell("secrets")
            env_file.update(line.split("=", 1) for line in (work / ".env").read_text().splitlines())
            http, grpc = free_ports()
            env_file.update(COMPOSE_PROJECT_NAME=project, HONUA_HTTP_PORT=str(http), HONUA_GRPC_PORT=str(grpc))
            (work / ".env").write_text("".join(f"{key}={value}\n" for key, value in env_file.items()))
            (work / ".env").chmod(0o600)
            shell("keyring")
            shell("start")
            base = f"http://localhost:{http}"
            wait_ready(base)
            shell("seed")
            shell("clients", 600)
            env["PATH"] = str(work / ".venv/bin") + os.pathsep + env["PATH"]
            (work / "quickstart.py").write_text(blocks["publish"])
            output = shell("query")
            match = re.fullmatch(r"published service quickstart layer (\d+)\n(.*)\s*", output, re.S)
            if not match:
                raise ValueError("Quickstart did not return a published layer and features")
            layer_id, payload = match.groups()
            features = json.loads(payload)
            verify_features(features)
            receipt["publishQuery"] = True
            run(compose + ["restart", "honua", "postgres", "redis"])
            wait_ready(base)
            # Query the persisted layer through the published SDK, without seed,
            # registration or publication again. No duplicate installation recipe.
            readback = run([str(work / ".venv/bin/python"), "-c", """
import json, sys
from honua_sdk import HonuaClient
env = dict(line.strip().split('=', 1) for line in open('.env') if '=' in line)
with HonuaClient('http://localhost:' + env['HONUA_HTTP_PORT'], api_key=env['HONUA_ADMIN_PASSWORD']) as client:
    print(json.dumps(client.query_features('quickstart', int(sys.argv[1]), out_fields=['id', 'name'], return_geometry=True)['features']))
""", layer_id])
            verify_features(json.loads(readback))
            receipt.update(restartReadback=True, layerId=int(layer_id), passed=True)
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            receipt["error"] = redact(str(error))
            print("Public install failed: " + receipt["error"], flush=True)
        finally:
            # Only this random project's resources; never prune shared Docker state.
            if (work / "compose.yaml").exists() and (work / ".env").exists():
                try:
                    run(compose + ["down", "--volumes", "--remove-orphans"])
                    receipt["cleanup"] = True
                except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                    receipt.update(cleanup=False, passed=False, cleanupError=redact(str(error)))
            (artifacts / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print("Public install proof: " + ("passed" if receipt["passed"] else "failed"))
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
