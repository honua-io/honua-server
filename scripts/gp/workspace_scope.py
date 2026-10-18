"""Authorized storage/transport adapter to the existing GP sandbox runner.

The independent daemon service credential authenticates the trusted server. All
actor assertions come from that server's current principal and workspace, never
from a model or browser. Project ownership and explicit workspace grants persist
outside source/output mounts. This module does not interpret or execute code.
"""
from __future__ import annotations

from dataclasses import dataclass
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import threading
import time
from typing import TYPE_CHECKING
import uuid

from workspace import ArtifactReader, DEFAULT_LIMITS, MAX_BYTES, read_regular, run, sanitized, snapshot, source_manifest
if TYPE_CHECKING:
    from workspace_service import Workspace

ID = re.compile(r"^[a-f0-9]{32}$")
DIGEST = re.compile(r"^[a-f0-9]{64}$")
COMMAND = re.compile(r"^[a-zA-Z0-9-]{1,80}$")


def bounded(value: object, maximum: int = 1024) -> str:
    if not isinstance(value, str) or not value or value != value.strip() or len(value) > maximum or any(ord(c) < 32 or ord(c) == 127 for c in value):
        raise PermissionError("Workspace scope is required.")
    return value


@dataclass(frozen=True)
class Access:
    actor: str
    owner: str
    workspace: str
    environment: str
    tenant: str | None = None
    delegated: bool = False

    @staticmethod
    def from_headers(headers) -> Access:
        delegated = headers.get("X-Honua-Workspace-Delegated", "false")
        if delegated not in {"true", "false"}: raise PermissionError("Invalid delegation assertion.")
        tenant = headers.get("X-Honua-Tenant-Id")
        return Access(bounded(headers.get("X-Honua-Actor-Id")), bounded(headers.get("X-Honua-Workspace-Owner-Id")),
                      bounded(headers.get("X-Honua-Workspace-Id"), 256), bounded(headers.get("X-Honua-Environment-Id"), 256),
                      None if tenant is None else bounded(tenant, 256), delegated == "true")

    def human(self) -> None:
        if self.delegated or self.actor != self.owner: raise PermissionError("Only the human owner can bind or revoke a project.")

    def binding(self) -> dict:
        return {"ownerActorId": self.owner, "tenantId": self.tenant, "environmentId": self.environment, "workspaceId": self.workspace}


def read_json(path: Path) -> dict:
    value = json.loads(read_regular(path, 2 * 1024 * 1024))
    if not isinstance(value, dict): raise ValueError("Invalid retained workspace record.")
    return value


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    data = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if len(data) > 2 * 1024 * 1024: raise ValueError("Workspace record budget exceeded.")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".record-", delete=False) as stream:
            temporary = Path(stream.name)
            os.chmod(temporary, 0o600)
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.replace(temporary, path)
        descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try: os.fsync(descriptor)
        finally: os.close(descriptor)
    finally:
        if temporary is not None: temporary.unlink(missing_ok=True)


class StoreLock:
    """One scoped transaction across threads and daemon processes sharing storage."""
    def __init__(self, path: Path):
        self.mutex = threading.RLock()
        self.depth = threading.local()
        self.file = path.open("a+b")
        os.chmod(path, 0o600)

    def __del__(self):
        self.file.close()

    def __enter__(self):
        self.mutex.acquire()
        depth = getattr(self.depth, "value", 0)
        try:
            if depth == 0:
                deadline = time.monotonic() + 10
                while True:
                    try:
                        fcntl.flock(self.file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                        break
                    except BlockingIOError:
                        if time.monotonic() >= deadline: raise FileExistsError("Workspace storage is busy; retry the same command.")
                        time.sleep(0.02)
            self.depth.value = depth + 1
            return self
        except BaseException:
            self.mutex.release()
            raise

    def __exit__(self, *_):
        self.depth.value -= 1
        if self.depth.value == 0: fcntl.flock(self.file.fileno(), fcntl.LOCK_UN)
        self.mutex.release()


class ScopedWorkspace:
    def __init__(self, workspace: Workspace):
        self.workspace = workspace
        self.directory = workspace.runs / ".workspace"
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.lock = StoreLock(self.directory / "transaction.lock")
        self.project_file = self.directory / "project.json"
        location = hashlib.sha256(str(workspace.project).encode()).hexdigest()
        with self.lock:
            if self.project_file.exists():
                self.project = read_json(self.project_file)
                if self.project.get("locationDigest") != location or self.project.get("image") != workspace.image or self.project.get("limits", DEFAULT_LIMITS.record()) != workspace.limits.record():
                    raise ValueError("Retained project binding belongs to another project or runtime image.")
            else:
                self.project = {"projectId": uuid.uuid4().hex, "locationDigest": location, "image": workspace.image,
                                "revision": 1, "sourceDigest": None, "ownerActorId": None, "tenantId": None,
                                "environmentId": None, "workspaces": [], "limits": workspace.limits.record()}
                atomic_json(self.project_file, self.project)
    def _authorize(self, access: Access, bound: bool = True) -> None:
        self.project = read_json(self.project_file)
        if self.project["ownerActorId"] is None:
            if bound: raise PermissionError("Bind this local project as its human owner before accessing it.")
            return
        if (access.owner != self.project["ownerActorId"] or access.tenant != self.project["tenantId"] or
                access.environment != self.project["environmentId"] or (not access.delegated and access.actor != access.owner)):
            raise PermissionError("Workspace project access denied.")
        if bound and access.workspace not in self.project["workspaces"]: raise PermissionError("Workspace project grant is absent or revoked.")

    def _refresh(self) -> dict:
        manifest = source_manifest(self.workspace.project, self.workspace.limits)
        if self.project["sourceDigest"] != manifest["digest"]:
            if self.project["sourceDigest"] is not None: self.project["revision"] += 1
            self.project["sourceDigest"] = manifest["digest"]
            atomic_json(self.project_file, self.project)
        return manifest

    def _identity(self, access: Access) -> dict:
        return {key: self.project[key] for key in ("projectId", "revision", "sourceDigest", "image")} | {
            "binding": None if self.project["ownerActorId"] is None else access.binding()}

    def identity(self, access: Access) -> dict:
        with self.lock:
            self._authorize(access, bound=False)
            if self.project["ownerActorId"] is not None: self._authorize(access)
            self._refresh()
            return self._identity(access)

    def bind(self, access: Access, project_id: str) -> dict:
        access.human()
        with self.lock:
            self._authorize(access, bound=False)
            if project_id != self.project["projectId"]: raise FileNotFoundError("Project not found.")
            if self.project["ownerActorId"] is None:
                self.project.update({"ownerActorId": access.owner, "tenantId": access.tenant, "environmentId": access.environment})
            if access.workspace not in self.project["workspaces"]:
                self.project["workspaces"].append(access.workspace)
                atomic_json(self.project_file, self.project)
            self._refresh()
            return self._identity(access)

    def revoke(self, access: Access, workspace_id: str) -> dict:
        access.human()
        with self.lock:
            self._authorize(access, bound=False)
            if workspace_id != access.workspace: raise PermissionError("Workspace scope mismatch.")
            self.project["workspaces"] = [value for value in self.project["workspaces"] if value != workspace_id]
            atomic_json(self.project_file, self.project)
            for run_id, event in self.workspace.cancellations.items():
                record = self.directory / "jobs" / f"{run_id}.json"
                if record.exists() and read_json(record).get("scope", {}).get("workspaceId") == workspace_id: event.set()
            return {"projectId": self.project["projectId"], "workspaceId": workspace_id, "revoked": True}

    def project_files(self, access: Access) -> dict:
        with self.lock:
            self._authorize(access); self._refresh()
            result = self.workspace.project_files() | self._identity(access)
            if access.delegated: result.pop("ideHandoff", None)
            return result

    def source(self, access: Access, relative: str) -> dict:
        with self.lock:
            self._authorize(access)
            path = self.workspace.source(relative)
            data = read_regular(path, 256 * 1024)
            return {"path": relative, "content": data.decode(), "sha256": hashlib.sha256(data).hexdigest()}

    def _expected(self, project_id: object, revision: object, digest: object | None = None) -> None:
        if project_id != self.project["projectId"]: raise FileNotFoundError("Project not found.")
        self._refresh()
        if type(revision) is not int or revision != self.project["revision"] or (digest is not None and digest != self.project["sourceDigest"]):
            raise FileExistsError("Project source or inputs changed; observe the current revision before continuing.")

    def write(self, access: Access, relative: str, body: dict, expected_sha: str) -> dict:
        with self.lock:
            self._authorize(access); self._expected(body.get("projectId"), body.get("expectedRevision"))
            if not isinstance(body.get("content"), str) or not DIGEST.fullmatch(expected_sha): raise ValueError("A source body and file digest are required.")
            result = self.workspace.write(relative, body["content"], expected_sha)
            self._refresh()
            return result | {key: self.project[key] for key in ("projectId", "revision", "sourceDigest")}

    def snapshot(self, access: Access, body: dict) -> dict:
        with self.lock:
            self._authorize(access); self._expected(body.get("projectId"), body.get("expectedRevision"), body.get("sourceDigest"))
            if not isinstance(body.get("sourceDigest"), str) or not DIGEST.fullmatch(body["sourceDigest"]): raise ValueError("An observed source digest is required.")
            snapshot_id = uuid.uuid4().hex
            directory = self.directory / "snapshots" / snapshot_id
            try:
                manifest = snapshot(self.workspace.project, directory / "source", self.workspace.limits)
                if manifest["digest"] != body["sourceDigest"]:
                    self._refresh()
                    raise FileExistsError("Project changed during snapshot capture; no run was submitted.")
                receipt = {"projectId": self.project["projectId"], "snapshotId": snapshot_id,
                           "snapshotDigest": manifest["digest"], "image": self.workspace.image, "revision": self.project["revision"], "limits": self.workspace.limits.record()}
                atomic_json(directory / "snapshot.json", receipt | {"scope": access.binding()})
                return receipt
            except Exception:
                shutil.rmtree(directory, ignore_errors=True)
                raise

    def _record_scope(self, access: Access, record: dict) -> None:
        self._authorize(access)
        if record.get("scope") != access.binding(): raise PermissionError("Run or snapshot belongs to another workspace.")

    def _snapshot(self, access: Access, snapshot_id: str, digest: str, legacy_artifact: bool = False) -> tuple[dict, Path]:
        if not ID.fullmatch(snapshot_id) or not DIGEST.fullmatch(digest): raise ValueError("Invalid immutable snapshot reference.")
        directory = self.directory / "snapshots" / snapshot_id
        record = read_json(directory / "snapshot.json")
        self._record_scope(access, record)
        if record["snapshotDigest"] != digest or record["image"] != self.workspace.image or record.get("limits", DEFAULT_LIMITS.record()) != self.workspace.limits.record(): raise FileExistsError("Snapshot identity mismatch.")
        manifest = source_manifest(directory / "source", self.workspace.limits)
        matches = manifest["digest"] == digest
        if legacy_artifact and "limits" not in record and self.workspace.limits == DEFAULT_LIMITS:
            # Older successful runs retain their original file-only digest and
            # implicit bounded profile. This compatibility is read-only; never
            # submit an execution under an unbound legacy limits identity.
            encoded = json.dumps(manifest["files"], sort_keys=True, separators=(",", ":")).encode()
            matches = matches or hashlib.sha256(encoded).hexdigest() == digest
        if not matches: raise FileExistsError("Retained snapshot bytes changed; no run was submitted.")
        return record, directory / "source"

    def get_snapshot(self, access: Access, snapshot_id: str) -> dict:
        with self.lock:
            self._authorize(access)
            if not ID.fullmatch(snapshot_id): raise ValueError("Invalid snapshot identity.")
            record = read_json(self.directory / "snapshots" / snapshot_id / "snapshot.json")
            self._snapshot(access, snapshot_id, record["snapshotDigest"])
            return {key: record[key] for key in ("projectId", "snapshotId", "snapshotDigest", "image", "revision")}

    def snapshot_source(self, access: Access, snapshot_id: str, relative: str) -> dict:
        with self.lock:
            observed = self.get_snapshot(access, snapshot_id)
            _, source = self._snapshot(access, snapshot_id, observed["snapshotDigest"])
            path = self.workspace.source(relative, source)
            data = read_regular(path, 256 * 1024)
            return {"path": relative, "content": data.decode(), "sha256": hashlib.sha256(data).hexdigest()}

    def _job(self, access: Access, run_id: str) -> dict:
        if not ID.fullmatch(run_id): raise ValueError("Invalid local run identity.")
        record = read_json(self.directory / "jobs" / f"{run_id}.json")
        self._record_scope(access, record)
        return record

    @staticmethod
    def _public(record: dict) -> dict:
        return {key: value for key, value in record.items() if key not in {"scope", "actorId", "requestHash"}}

    def start(self, access: Access, body: dict) -> dict:
        with self.lock:
            self._authorize(access)
            if body.get("projectId") != self.project["projectId"]: raise FileNotFoundError("Project not found.")
            snapshot_id, digest, command_id = body.get("snapshotId"), body.get("snapshotDigest"), body.get("commandId")
            if not all(isinstance(value, str) for value in (snapshot_id, digest, command_id)) or not COMMAND.fullmatch(command_id): raise ValueError("An exact snapshot and bounded command identity are required.")
            snapshot_record, _ = self._snapshot(access, snapshot_id, digest)
            accepted_limits = self.workspace.limits
            request = {"projectId": body["projectId"], "snapshotId": snapshot_id, "snapshotDigest": digest,
                       "image": self.workspace.image, "scope": access.binding(), "actorId": access.actor,
                       "acceptedRevision": snapshot_record["revision"], "limits": self.workspace.limits.record()}
            request_hash = hashlib.sha256(json.dumps(request, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            command_key = hashlib.sha256(json.dumps([access.binding(), access.actor, command_id], sort_keys=True).encode()).hexdigest()
            command_file = self.directory / "commands" / f"{command_key}.json"
            if command_file.exists():
                previous = read_json(command_file)
                if previous["requestHash"] != request_hash: raise FileExistsError("Command is already bound to another immutable request.")
                try: return self.status(access, previous["runId"])
                except FileNotFoundError:
                    return self._public(previous) | {"status": "unknown", "diagnostics": "The retained command receipt is unavailable; no new execution was submitted.", "artifacts": []}
            _, source = self._snapshot(access, snapshot_id, digest)
            active_path = self.directory / "active.json"
            if active_path.exists():
                active = read_json(active_path)
                active_job = self.directory / "jobs" / f'{active["runId"]}.json'
                previous = read_json(active_job) if active_job.exists() else {"status": "unknown"}
                if previous.get("status") not in {"succeeded", "failed", "cancelled", "timed-out"}:
                    terminal = self.workspace.runs / active["runId"] / "receipt.json"
                    if terminal.exists():
                        observed = read_json(terminal)
                        if observed.get("snapshotDigest") == previous.get("snapshotDigest") and observed.get("status") in {"succeeded", "failed", "cancelled", "timed-out"}:
                            previous |= {key: value for key, value in observed.items() if key in {"status", "diagnostics", "artifacts"}}
                            atomic_json(active_job, previous)
                    if previous.get("status") not in {"succeeded", "failed", "cancelled", "timed-out"}:
                        raise FileExistsError("A prior execution is active or its acceptance is unknown; reconcile that command before starting another.")
            if self.workspace.cancellations: raise FileExistsError("A sandbox run is active; cancel or wait before starting another.")
            run_id = uuid.uuid4().hex
            record = request | {"runId": run_id, "status": "queued", "requestHash": request_hash, "artifacts": []}
            # Persist the tombstone before accepting or starting execution. It outlives
            # run receipt cleanup; an uncertain crash never schedules a replacement.
            atomic_json(command_file, record)
            atomic_json(self.directory / "jobs" / f"{run_id}.json", record)
            atomic_json(active_path, {"runId": run_id})
            history_file = self._history_file(access)
            history = read_json(history_file).get("runs", []) if history_file.exists() else []
            atomic_json(history_file, {"runs": ([self._public(record)] + history)[:32]})
            cancellation = threading.Event()
            self.workspace.cancellations[run_id] = cancellation
            self.workspace.jobs[run_id] = self._public(record)

            def execute() -> None:
                try:
                    outcome = run(source, self.workspace.image, self.workspace.runs, 300, cancellation, run_id, limits=accepted_limits)
                    if outcome.get("snapshotDigest") != digest: raise ValueError("Executed snapshot digest did not match the accepted request.")
                    result = record | {key: value for key, value in outcome.items() if key in {"status", "diagnostics", "artifacts", "exitCode", "elapsedMs"}}
                except Exception as error:
                    result = record | {"status": "failed", "diagnostics": sanitized(str(error)), "artifacts": []}
                finally:
                    with self.lock:
                        atomic_json(self.directory / "jobs" / f"{run_id}.json", result)
                        self.workspace.jobs[run_id] = self._public(result)
                        self.workspace.cancellations.pop(run_id, None)
                        while len(self.workspace.jobs) > 128: self.workspace.jobs.pop(next(iter(self.workspace.jobs)))
            threading.Thread(target=execute, daemon=True).start()
            return self._public(record)

    def _history_file(self, access: Access) -> Path:
        key = hashlib.sha256(json.dumps(access.binding(), sort_keys=True).encode()).hexdigest()
        return self.directory / "history" / f"{key}.json"

    def list_runs(self, access: Access) -> dict:
        with self.lock:
            self._authorize(access)
            path = self._history_file(access)
            history = read_json(path).get("runs", []) if path.exists() else []
            runs = []
            for retained in history[:32]:
                try: observed = self.status(access, retained["runId"])
                except FileNotFoundError: observed = retained | {"status": "unknown"}
                item = {key: observed[key] for key in ("projectId", "snapshotId", "snapshotDigest", "runId", "status", "image")}
                if observed.get("diagnostics"): item["diagnostics"] = observed["diagnostics"][:512]
                runs.append(item)
            return {"runs": runs}

    def get_command(self, access: Access, command_id: str) -> dict:
        with self.lock:
            self._authorize(access)
            if not isinstance(command_id, str) or not COMMAND.fullmatch(command_id): raise ValueError("Invalid command identity.")
            key = hashlib.sha256(json.dumps([access.binding(), access.actor, command_id], sort_keys=True).encode()).hexdigest()
            record = read_json(self.directory / "commands" / f"{key}.json")
            self._record_scope(access, record)
            try: return self.status(access, record["runId"])
            except FileNotFoundError:
                return self._public(record) | {"status": "unknown", "diagnostics": "The retained command receipt is unavailable; no new execution was submitted.", "artifacts": []}

    def status(self, access: Access, run_id: str) -> dict:
        with self.lock:
            record = self._job(access, run_id)
            if record["status"] in {"queued", "running"} and run_id not in self.workspace.cancellations:
                # Restart recovery observes actual durable receipts, never reexecutes.
                receipt = self.workspace.runs / run_id / "receipt.json"
                if receipt.exists():
                    observed = read_json(receipt)
                    if observed.get("snapshotDigest") != record["snapshotDigest"]: raise ValueError("Retained run snapshot mismatch.")
                    record |= {key: value for key, value in observed.items() if key in {"status", "diagnostics", "artifacts", "exitCode", "elapsedMs"}}
                    atomic_json(self.directory / "jobs" / f"{run_id}.json", record)
                else:
                    record = record | {"status": "unknown", "diagnostics": "Runner acceptance is unknown after restart; no new execution was submitted.", "artifacts": []}
            return self._public(record)

    def cancel(self, access: Access, run_id: str) -> dict:
        with self.lock:
            self._job(access, run_id)
            observed = self.status(access, run_id)
            if observed["status"] in {"succeeded", "failed", "cancelled", "timed-out"}: return observed
            event = self.workspace.cancellations.get(run_id)
            if event is None: raise FileExistsError("Run is not attached to this runner; acceptance remains unknown.")
            event.set()
            return observed | {"cancellationRequested": True}

    def artifact(self, access: Access, run_id: str, relative: str) -> ArtifactReader:
        with self.lock:
            self._job(access, run_id)
            observed = self.status(access, run_id)
            if observed["status"] != "succeeded": raise ValueError("Only successful local runs expose declared artifacts.")
            accepted, _ = self._snapshot(access, observed["snapshotId"], observed["snapshotDigest"], legacy_artifact=True)
            if accepted["projectId"] != observed["projectId"] or observed.get("acceptedRevision", accepted["revision"]) != accepted["revision"]:
                raise PermissionError("Artifact snapshot version binding changed.")
            pinned = {key: observed.get(key) for key in ("projectId", "acceptedRevision", "snapshotId", "snapshotDigest", "image", "limits", "artifacts")}
            self.workspace.jobs[run_id] = observed
        def authorize() -> None:
            with self.lock:
                current = self.status(access, run_id)
                if current["status"] != "succeeded" or any(current.get(key) != value for key, value in pinned.items()):
                    raise PermissionError("Immutable artifact job binding changed.")
                snapshot_record = read_json(self.directory / "snapshots" / observed["snapshotId"] / "snapshot.json")
                self._record_scope(access, snapshot_record)
                if snapshot_record != accepted: raise PermissionError("Artifact snapshot receipt changed.")
                if pinned["limits"] not in (None, self.workspace.limits.record()):
                    raise PermissionError("Immutable artifact limits changed.")
        return self.workspace.artifact(run_id, relative, authorize, pinned | {"runId": run_id, "path": relative, "acceptedRevision": accepted["revision"]})
