#!/usr/bin/env python3
"""Qualify installed workspace bootstrap/serve, without importing checkout code.

Run under with-build-lock. Execution fixtures are separately qualified by
qualify_workspace.py against the exact released digest-pinned sandbox image.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def qualify(devkit: Path, image: str, root: Path) -> dict:
    root.mkdir(parents=True, exist_ok=False)
    command = str(devkit.resolve(strict=True))
    bootstrap = root / "bootstrap"
    subprocess.run([command, "workspace", "bootstrap", "--output", str(bootstrap)], check=True, timeout=60)
    provenance = json.loads((bootstrap / "workspace-provenance.json").read_text())
    for name, expected in provenance["files"].items():
        assert hashlib.sha256((bootstrap / name).read_bytes()).hexdigest() == expected
    assert provenance["devkitVersion"] and "workspace_scope.py" in provenance["files"]
    subprocess.run([command, "init", "--language", "python", "--name", "workspace-check", "--output", str(root)], check=True, timeout=60)
    project = root / "workspace-check"
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    key = secrets.token_hex(32)
    headers = {"X-Honua-Workspace-Key": key, "X-Honua-Actor-Id": "human:qualification",
               "X-Honua-Workspace-Owner-Id": "human:qualification", "X-Honua-Workspace-Id": "qualification-workspace",
               "X-Honua-Environment-Id": "qualification-target", "X-Honua-Tenant-Id": "qualification-tenant",
               "X-Honua-Workspace-Delegated": "false"}

    def request(path: str, method: str = "GET", value: dict | None = None, extra: dict | None = None):
        request_headers = headers | (extra or {})
        if value is not None: request_headers["Content-Type"] = "application/json"
        data = None if value is None else json.dumps(value).encode()
        with urlopen(Request(f"http://127.0.0.1:{port}/{path}", data=data, headers=request_headers, method=method), timeout=10) as response:
            return json.load(response)

    with (root / "service.log").open("w") as log:
        process = subprocess.Popen([command, "workspace", "serve", "--project", str(project), "--runs", str(root / "runs"),
                                    "--image", image, "--port", str(port)],
                                   env=os.environ | {"HONUA_GP_WORKSPACE_KEY": key}, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            deadline = time.monotonic() + 30
            while True:
                try:
                    identity = request("project/identity")
                    break
                except (URLError, ConnectionError):
                    if process.poll() is not None or time.monotonic() >= deadline: raise RuntimeError("Installed workspace service did not become ready.")
                    time.sleep(0.1)
            assert identity["binding"] is None and "files" not in identity
            identity = request("project/bind", "POST", {"projectId": identity["projectId"]})
            assert identity["binding"]["workspaceId"] == headers["X-Honua-Workspace-Id"]
            try:
                request("project/bind", "POST", {"projectId": identity["projectId"]}, {"X-Honua-Workspace-Delegated": "true"})
                raise AssertionError("Delegated owner was permitted to bind.")
            except HTTPError as error: assert error.code == 403
            visible = request("project")
            delegated = request("project", extra={"X-Honua-Actor-Id": "agent:qualification", "X-Honua-Workspace-Delegated": "true"})
            assert "ideHandoff" in visible and "ideHandoff" not in delegated
            source = next(item for item in visible["files"] if item["path"].endswith(".py") and item["path"].startswith("process/"))
            captured = request("snapshots", "POST", {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "sourceDigest": identity["sourceDigest"]})
            saved = request("sources/" + source["path"], "PUT", {"projectId": identity["projectId"], "expectedRevision": identity["revision"], "content": source["content"] + "\n# corrected immutable snapshot check\n"}, {"If-Match": source["sha256"]})
            assert saved["revision"] > identity["revision"] and saved["sourceDigest"] != captured["snapshotDigest"]
            frozen = request("snapshots/" + captured["snapshotId"] + "/sources/" + source["path"])
            assert frozen["content"] == source["content"] and frozen["sha256"] == source["sha256"]
            assert request("runs") == {"runs": []}
            try:
                request("project/identity", extra={"X-Honua-Workspace-Id": "ungranted-workspace"})
                raise AssertionError("Foreign workspace read project metadata.")
            except HTTPError as error: assert error.code == 403
            process.send_signal(signal.SIGTERM)
            assert process.wait(timeout=20) == 0, "Installed launcher/service did not exit cleanly on SIGTERM."
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)
    evidence = {"devkitVersion": provenance["devkitVersion"], "payload": provenance["files"], "image": image,
                "projectId": identity["projectId"], "snapshot": captured, "saved": saved,
                "checks": ["installed-bootstrap", "payload-hashes", "installed-serve", "human-binding", "delegated-bind-denied", "workspace-grant", "immutable-source", "conditional-source", "run-discovery", "sigterm-shutdown"]}
    (root / "qualification.json").write_text(json.dumps(evidence, indent=2) + "\n")
    return evidence


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--devkit", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--root", type=Path, required=True)
    options = parser.parse_args()
    print(json.dumps(qualify(options.devkit, options.image, options.root.resolve())))
