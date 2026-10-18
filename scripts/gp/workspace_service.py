#!/usr/bin/env python3
"""Loopback-only GP editor bridge. Console keeps its access key server-side."""
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
import threading
import uuid
from urllib.parse import unquote, urlsplit

from workspace import EXCLUDED, command, run, sanitized, snapshot

EDITABLE = {".py", ".cs", ".csproj", ".json", ".yaml", ".yml", ".lock", ".toml"}
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

    def source(self, relative: str) -> Path:
        relative_path = Path(relative)
        if relative_path.is_absolute() or any(part in {"..", "."} | EXCLUDED for part in relative_path.parts):
            raise ValueError("Invalid source path.")
        if relative_path.suffix not in EDITABLE: raise ValueError("Unsupported source file type.")
        target = self.project / relative_path
        current = target
        while current != self.project:
            if current.is_symlink(): raise ValueError("Source links are not editable.")
            current = current.parent
        if not target.resolve().is_relative_to(self.project): raise ValueError("Source path is outside the project.")
        return target

    def project_files(self) -> dict:
        files = []
        total = 0
        with self.lock:
            for root, directories, names in os.walk(self.project, followlinks=False):
                directories[:] = [name for name in directories if name not in EXCLUDED and not (Path(root) / name).is_symlink()]
                for name in sorted(names):
                    candidate = Path(root) / name
                    if candidate.suffix not in EDITABLE or candidate.is_symlink(): continue
                    path = self.source(candidate.relative_to(self.project).as_posix())
                    if path.stat().st_size > MAX_SOURCE_BYTES: continue
                    content = path.read_text()
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
            actual = hashlib.sha256(path.read_bytes()).hexdigest()
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
        return json.loads(path.read_text())

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
        if hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]: raise ValueError("Artifact content changed.")
        return path, item


def serve(workspace: Workspace, key: str, port: int) -> None:
    if len(key) < 32: raise ValueError("HONUA_GP_WORKSPACE_KEY must contain at least 32 characters.")
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_: object) -> None: pass
        def do_GET(self) -> None: self.handle_request()
        def do_POST(self) -> None: self.handle_request()
        def do_PUT(self) -> None: self.handle_request()
        def reply(self, status: int, value: dict) -> None:
            data = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("content-type", "application/json")
            self.send_header("cache-control", "no-store")
            self.send_header("content-length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        def handle_request(self) -> None:
            if self.headers.get("Origin") or self.headers.get("Host") != f"127.0.0.1:{port}" or not hmac.compare_digest(self.headers.get("X-Honua-Workspace-Key", "").encode(), key.encode()):
                self.reply(403, {"error": "Workspace access denied."}); return
            try:
                raw = urlsplit(self.path)
                if raw.query or raw.fragment: raise ValueError("Query parameters are not supported.")
                path = unquote(raw.path).strip("/")
                length = int(self.headers.get("content-length", "0"))
                if length < 0 or length > 2 * MAX_SOURCE_BYTES: raise ValueError("Request budget exceeded.")
                body = json.loads(self.rfile.read(length)) if length else {}
                if self.command == "GET" and path == "project": result = workspace.project_files()
                elif self.command == "PUT" and path.startswith("sources/"):
                    result = workspace.write(path[8:], body["content"], self.headers.get("If-Match", "").strip('"'))
                elif self.command == "POST" and path == "runs": result = workspace.start()
                elif re.fullmatch("runs/[a-f0-9]{32}", path) and self.command == "GET": result = workspace.status(path[5:])
                elif re.fullmatch("runs/[a-f0-9]{32}/cancel", path) and self.command == "POST": result = workspace.cancel(path.split("/")[1])
                elif path.startswith("runs/") and "/artifacts/" in path and self.command == "GET":
                    run_id, relative = path[5:].split("/artifacts/", 1)
                    artifact, item = workspace.artifact(run_id, relative)
                    self.send_response(200); self.send_header("content-type", "application/octet-stream")
                    self.send_header("content-length", str(item["bytes"])); self.send_header("cache-control", "no-store")
                    self.end_headers()
                    with artifact.open("rb") as stream: shutil.copyfileobj(stream, self.wfile)
                    return
                else: self.reply(404, {"error": "Workspace route not found."}); return
                self.reply(200, result)
            except FileExistsError as error: self.reply(409, {"error": str(error)})
            except FileNotFoundError: self.reply(404, {"error": "Workspace resource not found."})
            except (ValueError, KeyError, UnicodeError) as error: self.reply(400, {"error": sanitized(str(error))})
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--port", type=int, default=7194)
    options = parser.parse_args()
    serve(Workspace(options.project, options.image, options.runs), os.environ.get("HONUA_GP_WORKSPACE_KEY", ""), options.port)
