#!/usr/bin/env python3
"""Scoped GP editor bridge. The canonical server retains its service credential."""
from __future__ import annotations

import argparse
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import shutil
import signal
import time
import threading
import uuid
from urllib.parse import unquote, urlsplit

from workspace import EXCLUDED, MAX_BYTES, command, read_regular, run, sanitized, secret_path, snapshot

SOURCE_EXCLUDED = EXCLUDED | {".honua"}
EDITABLE = {".py", ".cs", ".csproj", ".json", ".yaml", ".yml", ".lock", ".toml", ".geojson", ".csv", ".wkt"}
MAX_SOURCE_BYTES = 256 * 1024


class Workspace:
    def __init__(self, project: Path, image: str, runs: Path):
        self.project = project.resolve(strict=True)
        self.image = image
        self.runs = runs.resolve()
        if self.runs.is_relative_to(self.project): raise ValueError("Run storage must be outside the project.")
        command(image, self.project, self.runs, "validate-image")
        self.lock = threading.Lock()
        self.jobs: dict[str, dict] = {}
        self.cancellations: dict[str, threading.Event] = {}

    def source(self, relative: str, project: Path | None = None) -> Path:
        root = self.project if project is None else project
        relative_path = Path(relative)
        if secret_path(relative_path): raise ValueError("Credential files are outside the editable project scope.")
        if relative_path.is_absolute() or any(part in {"..", "."} | SOURCE_EXCLUDED for part in relative_path.parts):
            raise ValueError("Invalid source path.")
        if relative_path.suffix not in EDITABLE: raise ValueError("Unsupported source file type.")
        target = root / relative_path
        current = target
        while current != root:
            if current.is_symlink(): raise ValueError("Source links are not editable.")
            current = current.parent
        if not target.resolve().is_relative_to(root): raise ValueError("Source path is outside the project.")
        return target

    def project_files(self) -> dict:
        files = []
        total = 0
        with self.lock:
            for root, directories, names in os.walk(self.project, followlinks=False):
                directories[:] = [name for name in directories if name not in SOURCE_EXCLUDED and not secret_path(Path(root).relative_to(self.project) / name) and not (Path(root) / name).is_symlink()]
                for name in sorted(names):
                    candidate = Path(root) / name
                    if candidate.suffix not in EDITABLE or candidate.is_symlink() or secret_path(candidate.relative_to(self.project)): continue
                    path = self.source(candidate.relative_to(self.project).as_posix())
                    content = read_regular(path, MAX_SOURCE_BYTES).decode()
                    total += len(content.encode())
                    if total > 8 * 1024 * 1024: raise ValueError("Source editor size budget exceeded.")
                    files.append({"path": path.relative_to(self.project).as_posix(), "content": content,
                                  "sha256": hashlib.sha256(content.encode()).hexdigest()})
        return {"files": files, "image": self.image, "ideHandoff": {"kind": "vscode", "workspace": str(self.project)}}

    def write(self, relative: str, content: str, expected: str) -> dict:
        data = content.encode()
        if len(data) > MAX_SOURCE_BYTES: raise ValueError("Source file exceeds the editor budget.")
        with self.lock:
            path = self.source(relative)
            if not path.is_file(): raise ValueError("Only existing project sources may be edited.")
            actual = hashlib.sha256(read_regular(path, MAX_SOURCE_BYTES)).hexdigest()
            if not hmac.compare_digest(actual.encode(), expected.encode()): raise FileExistsError("Source changed; reload before saving.")
            temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
            try:
                temporary.write_bytes(data)
                temporary.replace(path)
            finally:
                temporary.unlink(missing_ok=True)
            return {"path": relative, "sha256": hashlib.sha256(data).hexdigest()}

    def start(self) -> dict:
        run_id = uuid.uuid4().hex
        stage = self.runs / "staging" / run_id
        with self.lock:
            if self.cancellations: raise ValueError("A sandbox run is active; cancel or wait before starting another.")
            manifest = snapshot(self.project, stage)
            self.jobs[run_id] = {"runId": run_id, "status": "queued", "snapshotDigest": manifest["digest"]}
            cancel = threading.Event()
            self.cancellations[run_id] = cancel
        def execute() -> None:
            try:
                receipt = run(stage, self.image, self.runs, 300, cancel, run_id)
                with self.lock: self.jobs[run_id] = receipt
            except Exception as error:
                with self.lock: self.jobs[run_id] = {"runId": run_id, "status": "failed", "snapshotDigest": manifest["digest"], "diagnostics": sanitized(str(error)), "artifacts": []}
            finally:
                shutil.rmtree(stage, ignore_errors=True)
                with self.lock:
                    self.cancellations.pop(run_id, None)
                    while len(self.jobs) > 128: self.jobs.pop(next(iter(self.jobs)))
        threading.Thread(target=execute, daemon=True).start()
        return dict(self.jobs[run_id])

    def status(self, run_id: str) -> dict:
        if not re.fullmatch("[a-f0-9]{32}", run_id): raise ValueError("Invalid run ID.")
        with self.lock:
            if run_id in self.jobs: return dict(self.jobs[run_id])
        path = self.runs / run_id / "receipt.json"
        if not path.is_file(): raise FileNotFoundError("Run not found.")
        return json.loads(read_regular(path, 1024 * 1024))

    def cancel(self, run_id: str) -> dict:
        status = self.status(run_id)
        if status["status"] in {"succeeded", "failed", "cancelled", "timed-out"}: return status
        with self.lock:
            event = self.cancellations.get(run_id)
            if event is None: raise ValueError("Run is no longer attached to this runner.")
            event.set()
        return {**status, "cancellationRequested": True}

    def artifact(self, run_id: str, relative: str) -> tuple[Path, dict]:
        status = self.status(run_id)
        if status["status"] != "succeeded": raise ValueError("Failed or unfinished runs cannot expose successful artifacts.")
        item = next((item for item in status.get("artifacts", []) if item["path"] == relative), None)
        if item is None: raise FileNotFoundError("Artifact not found.")
        root = self.runs / run_id / "output"
        path = root / relative
        if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()): raise ValueError("Invalid artifact path.")
        if hashlib.sha256(read_regular(path, min(item["bytes"], MAX_BYTES))).hexdigest() != item["sha256"]: raise ValueError("Artifact content changed.")
        return path, item


def create_server(workspace: Workspace, key: str, port: int, bind_address: str = "127.0.0.1", allowed_hosts: tuple[str, ...] = ()) -> ThreadingHTTPServer:
    import ipaddress
    import socket
    from workspace_scope import Access, ScopedWorkspace
    if len(key) < 32: raise ValueError("HONUA_GP_WORKSPACE_KEY must contain at least 32 characters.")
    address = ipaddress.ip_address(bind_address)
    private_networks = [ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7")]
    if address.is_unspecified or not (address.is_loopback or any(address.version == network.version and address in network for network in private_networks)):
        raise ValueError("Bind only to an explicit loopback or private interface.")
    if not address.is_loopback and not allowed_hosts: raise ValueError("A private interface requires an explicit allowed Host authority.")
    if any(not re.fullmatch(r"(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\]):[0-9]{1,5}", value) for value in allowed_hosts):
        raise ValueError("Allowed Host authorities must be exact host:port values.")
    scoped = ScopedWorkspace(workspace)
    hosts = {value.lower() for value in allowed_hosts}
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_: object) -> None: pass
        def do_GET(self) -> None: self.handle_request()
        def do_POST(self) -> None: self.handle_request()
        def do_PUT(self) -> None: self.handle_request()
        def do_DELETE(self) -> None: self.handle_request()
        def reply(self, status: int, value: dict) -> None:
            data = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("content-type", "application/json")
            self.send_header("cache-control", "no-store")
            self.send_header("content-length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        def handle_request(self) -> None:
            self.connection.settimeout(10)
            sensitive = ("Host", "Content-Length", "X-Honua-Workspace-Key", "X-Honua-Actor-Id", "X-Honua-Workspace-Owner-Id", "X-Honua-Workspace-Id", "X-Honua-Environment-Id", "X-Honua-Tenant-Id", "X-Honua-Workspace-Delegated")
            if (any(len(self.headers.get_all(name, [])) > 1 for name in sensitive) or self.headers.get("Transfer-Encoding") or
                    self.headers.get("Origin") or self.headers.get("Host", "").lower() not in hosts or
                    not hmac.compare_digest(self.headers.get("X-Honua-Workspace-Key", "").encode(), key.encode())):
                self.reply(403, {"error": "Workspace access denied."}); return
            try:
                access = Access.from_headers(self.headers)
                raw = urlsplit(self.path)
                if raw.query or raw.fragment or re.search(r"%(?:25|5c)", raw.path, re.I): raise ValueError("Unsupported workspace URL.")
                path = unquote(raw.path).strip("/")
                if "\\" in path or any(ord(value) < 32 for value in path): raise ValueError("Invalid workspace path.")
                length = int(self.headers.get("content-length", "0"))
                if length < 0 or length > 2 * 1024 * 1024: raise ValueError("Request budget exceeded.")
                def properties(pairs):
                    result = {}
                    for name, value in pairs:
                        if name in result: raise ValueError("Duplicate request properties are not accepted.")
                        result[name] = value
                    return result
                data = self.rfile.read(length)
                if len(data) != length: raise ValueError("Incomplete request body.")
                body = json.loads(data, object_pairs_hook=properties) if length else {}
                if not isinstance(body, dict): raise ValueError("An object request is required.")
                def fields(*names):
                    if set(body) != set(names): raise ValueError("Unexpected or missing request properties.")
                if self.command == "GET" and path == "project/identity": result = scoped.identity(access)
                elif self.command == "POST" and path == "project/bind":
                    fields("projectId"); result = scoped.bind(access, body["projectId"])
                elif self.command == "DELETE" and path.startswith("project/bindings/"):
                    fields(); result = scoped.revoke(access, path[len("project/bindings/"):])
                elif self.command == "GET" and path == "project": result = scoped.project_files(access)
                elif self.command == "GET" and path.startswith("sources/"): result = scoped.source(access, path[8:])
                elif self.command == "PUT" and path.startswith("sources/"):
                    fields("projectId", "expectedRevision", "content")
                    result = scoped.write(access, path[8:], body, self.headers.get("If-Match", "").strip('"'))
                elif self.command == "POST" and path == "snapshots":
                    fields("projectId", "expectedRevision", "sourceDigest"); result = scoped.snapshot(access, body)
                elif self.command == "GET" and re.fullmatch("snapshots/[a-f0-9]{32}", path):
                    result = scoped.get_snapshot(access, path.split("/")[1])
                elif self.command == "GET" and re.fullmatch("snapshots/[a-f0-9]{32}/sources/.+", path):
                    snapshot_id, relative = path[len("snapshots/"):].split("/sources/", 1)
                    result = scoped.snapshot_source(access, snapshot_id, relative)
                elif self.command == "GET" and path == "runs": result = scoped.list_runs(access)
                elif self.command == "POST" and path == "runs":
                    fields("projectId", "snapshotId", "snapshotDigest", "commandId"); result = scoped.start(access, body)
                elif path.startswith("commands/") and self.command == "GET": result = scoped.get_command(access, path[len("commands/"):])
                elif re.fullmatch("runs/[a-f0-9]{32}", path) and self.command == "GET": result = scoped.status(access, path[5:])
                elif re.fullmatch("runs/[a-f0-9]{32}/cancel", path) and self.command == "POST": result = scoped.cancel(access, path.split("/")[1])
                elif path.startswith("runs/") and "/artifacts/" in path and self.command == "GET":
                    run_id, relative = path[5:].split("/artifacts/", 1)
                    artifact, item = scoped.artifact(access, run_id, relative)
                    content = read_regular(artifact, min(item["bytes"], MAX_BYTES))
                    if len(content) != item["bytes"] or hashlib.sha256(content).hexdigest() != item["sha256"]: raise ValueError("Artifact changed.")
                    self.send_response(200); self.send_header("content-type", "application/octet-stream")
                    self.send_header("content-length", str(item["bytes"])); self.send_header("cache-control", "no-store")
                    self.end_headers(); self.wfile.write(content)
                    return
                else: self.reply(404, {"error": "Workspace route not found."}); return
                self.reply(200, result)
            except PermissionError: self.reply(403, {"error": "Workspace access denied."})
            except FileExistsError as error: self.reply(409, {"error": sanitized(str(error))})
            except FileNotFoundError: self.reply(404, {"error": "Workspace resource not found."})
            except (ValueError, KeyError, TypeError, UnicodeError) as error: self.reply(400, {"error": sanitized(str(error))})
            except OSError: self.reply(500, {"error": "Workspace storage is unavailable."})
    class Server(ThreadingHTTPServer):
        address_family = socket.AF_INET6 if address.version == 6 else socket.AF_INET
    server = Server((bind_address, port), Handler)
    if not hosts:
        host = f"[{bind_address}]" if address.version == 6 else bind_address
        hosts.add(f"{host}:{server.server_address[1]}")
    return server


def serve(workspace: Workspace, key: str, port: int, bind_address: str = "127.0.0.1", allowed_hosts: tuple[str, ...] = ()) -> None:
    def interrupt(*_) -> None:
        raise KeyboardInterrupt
    previous = signal.signal(signal.SIGTERM, interrupt) if threading.current_thread() is threading.main_thread() else None
    try:
        with create_server(workspace, key, port, bind_address, allowed_hosts) as server:
            try:
                server.serve_forever()
            except KeyboardInterrupt:
                pass
            finally:
                cancel_active(workspace)
    finally:
        if previous is not None: signal.signal(signal.SIGTERM, previous)


def cancel_active(workspace: Workspace, timeout: float = 10) -> None:
    """Let the existing runner terminate its containers before daemon shutdown."""
    for event in list(workspace.cancellations.values()): event.set()
    deadline = time.monotonic() + timeout
    while workspace.cancellations and time.monotonic() < deadline:
        time.sleep(0.02)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--port", type=int, default=7194)
    parser.add_argument("--bind-address", default="127.0.0.1")
    parser.add_argument("--allowed-host", action="append", default=[])
    options = parser.parse_args()
    serve(Workspace(options.project, options.image, options.runs), os.environ.get("HONUA_GP_WORKSPACE_KEY", ""), options.port,
          options.bind_address, tuple(options.allowed_host))
