#!/usr/bin/env python3
"""Read-only, opt-in OS/filesystem observations for governed GP qualification.

Run in a separate ECS Exec process, never in the job executor. This program does
not cancel, kill, release barriers, repair state, or certify an AWS candidate.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import time


SCHEMA = "honua.gp-native-observation/v1"
NATIVE_EXECUTABLES = ("/usr/bin/ogr2ogr", "/usr/local/bin/ogr2ogr")
TOKEN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,127}\Z")
# Match GeoprocessingOutputStagingOptions; store names and prefixes are not job tokens.
STORE_REFERENCE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,159}\Z")
KEY_PREFIX = re.compile(r"(?!.*\.\.)[A-Za-z0-9][A-Za-z0-9._/-]{0,158}\Z")
RELATIVE_KEY = re.compile(r"(?!.*\.\.)[A-Za-z0-9][A-Za-z0-9._/-]*\Z")
HEX = re.compile(r"[0-9a-f]{64}\Z")
MAX_FILES, MAX_BYTES = 4096, 128 * 1024 * 1024
MAX_ENTRIES, MAX_DEPTH, MAX_BARRIER_AGE_SECONDS = 4096, 32, 300


class Refused(ValueError):
    """A safe diagnostic code, with no private file contents or system errors."""


def require(condition, code):
    if not condition:
        raise Refused(code)


def open_directory(path):
    """Refuse symlinks in every component, including trusted mount roots."""
    path = Path(path)
    require(path.is_absolute() and ".." not in path.parts, "directory-path")
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY)
    try:
        for part in path.parts[1:]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise


def read_json(path):
    path = Path(path).absolute()
    directory = open_directory(path.parent)
    try:
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
    finally:
        os.close(directory)
    with os.fdopen(fd, "rb") as stream:
        require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), "non-regular-json")
        data = stream.read(65537)
    require(len(data) <= 65536, "oversized-json")
    value = json.loads(data)
    require(isinstance(value, dict), "invalid-json-object")
    return value, hashlib.sha256(data).hexdigest()


def policy(path):
    value, digest = read_json(path)
    require(set(value) == {"schema", "enabled", "role", "barrierRoot", "scratchRoot", "outputRoot",
                          "keyPrefix", "storeReference", "configurationDigest"}, "policy-fields")
    require(value["schema"] == "honua.gp-observer-policy/v1" and value["enabled"] is True,
            "qualification-disabled")
    require(value["role"] in ("worker", "store-observer"), "policy-role")
    for key in ("barrierRoot", "scratchRoot", "outputRoot"):
        if key == "scratchRoot" and value["role"] == "store-observer":
            require(value[key] is None, "store-observer-scope")
            continue
        if key == "barrierRoot" and value["role"] == "store-observer" and value[key] is None:
            continue
        root = Path(value[key])
        require(root.is_absolute() and str(root.resolve(strict=True)) == str(root), "policy-root")
        require(root.is_dir(), "policy-root")
    prefix = value["keyPrefix"]
    require(isinstance(prefix, str) and KEY_PREFIX.fullmatch(prefix), "policy-prefix")
    require(isinstance(value["storeReference"], str) and STORE_REFERENCE.fullmatch(value["storeReference"])
            and isinstance(value["configurationDigest"], str) and HEX.fullmatch(value["configurationDigest"]),
            "policy-store-identity")
    return value, digest


def process(pid):
    require(type(pid) is int and pid > 0, "invalid-pid")
    try:
        text = Path(f"/proc/{pid}/stat").read_text()
    except FileNotFoundError:
        return None
    # comm may contain spaces and parentheses. Fields after the LAST ')' start
    # with state (3); starttime is field 22, not a wall-clock timestamp.
    fields = text[text.rindex(")") + 2:].split()
    require(len(fields) >= 20 and fields[0] not in ("Z", "X"), "invalid-or-zombie-process")
    return {"pid": pid, "parentPid": int(fields[1]), "startTicks": int(fields[19])}


def inventory(root, relative):
    """Open each component relative to an owned directory FD; never follow links."""
    require(isinstance(relative, str) and RELATIVE_KEY.fullmatch(relative), "inventory-path")
    fd = open_directory(root)
    try:
        # The production filesystem store normalizes single-dot and repeated
        # separators. Traversal is refused above; every remaining open still
        # uses O_NOFOLLOW so normalization cannot introduce a symlink escape.
        for part in Path(relative).parts:
            try:
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            except FileNotFoundError:
                return {"state": "absent", "files": [], "bytes": 0}
            os.close(fd)
            fd = child
        root_stat = os.fstat(fd)
        rows, total, entries = [], 0, 0

        def walk(directory, prefix, depth=0):
            nonlocal total, entries
            require(depth <= MAX_DEPTH, "inventory-depth")
            directory_before = os.fstat(directory)
            names = []
            with os.scandir(directory) as listing:
                for item in listing:
                    entries += 1
                    require(entries <= MAX_ENTRIES, "inventory-limit")
                    names.append(item.name)
            for name in sorted(names):
                require(name not in (".", ".."), "inventory-name")
                before = os.stat(name, dir_fd=directory, follow_symlinks=False)
                require(not stat.S_ISLNK(before.st_mode), "inventory-symlink")
                if stat.S_ISDIR(before.st_mode):
                    nested = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
                    try:
                        require((os.fstat(nested).st_dev, os.fstat(nested).st_ino) ==
                                (before.st_dev, before.st_ino), "inventory-race")
                        walk(nested, prefix + name + "/", depth + 1)
                    finally:
                        os.close(nested)
                    continue
                require(stat.S_ISREG(before.st_mode), "inventory-special-file")
                require(len(rows) < MAX_FILES and total + before.st_size <= MAX_BYTES, "inventory-limit")
                item = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
                with os.fdopen(item, "rb") as stream:
                    first = os.fstat(stream.fileno())
                    require((first.st_dev, first.st_ino, first.st_size, first.st_mtime_ns) ==
                            (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns), "inventory-race")
                    digest, size = hashlib.sha256(), 0
                    while chunk := stream.read(65536):
                        size += len(chunk)
                        require(total + size <= MAX_BYTES, "inventory-limit")
                        digest.update(chunk)
                    last = os.fstat(stream.fileno())
                require((first.st_size, first.st_mtime_ns, first.st_ino) ==
                        (last.st_size, last.st_mtime_ns, last.st_ino) and size == first.st_size,
                        "inventory-race")
                total += size
                rows.append({"path": prefix + name, "bytes": size, "sha256": digest.hexdigest()})
            directory_after = os.fstat(directory)
            require((directory_before.st_ino, directory_before.st_mtime_ns) ==
                    (directory_after.st_ino, directory_after.st_mtime_ns), "inventory-race")

        walk(fd, "")
        return {"state": "present", "device": root_stat.st_dev, "inode": root_stat.st_ino,
                "files": rows, "bytes": total}
    finally:
        os.close(fd)


def observe(config, job, nonce, phase, expected=None):
    require(isinstance(job, str) and TOKEN.fullmatch(job), "invalid-job")
    require(isinstance(nonce, str) and re.fullmatch(r"[0-9a-f]{32}", nonce), "invalid-nonce")
    require(phase in ("before", "after", "started", "store"), "invalid-phase")
    value, policy_digest = policy(config)
    marker, marker_digest = read_json(Path(value["outputRoot"]) / ".honua-gp-store.json")
    require(marker.get("Provider") == "local" and marker.get("PersistenceClass") == "shared-persistent"
            and marker.get("StoreReference") == value["storeReference"]
            and marker.get("ConfigurationDigest") == value["configurationDigest"], "store-attestation")
    result = {"schema": SCHEMA, "status": "observed", "qualification": False, "jobId": job,
              "nonce": nonce, "phase": phase, "observedAtUnixNs": time.time_ns(),
              "policySha256": policy_digest, "store": {"reference": value["storeReference"],
                  "configurationDigest": value["configurationDigest"], "attestationSha256": marker_digest},
              "outputInventory": inventory(value["outputRoot"], value["keyPrefix"] + "/" + job)}
    scope = {key: value[key] for key in ("role", "barrierRoot", "scratchRoot", "outputRoot", "keyPrefix")}
    result["scopeSha256"] = hashlib.sha256(json.dumps(scope, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    if phase == "store":
        return result
    require(value["barrierRoot"] is not None, "barrier-policy-required")
    ready, barrier_digest = read_json(Path(value["barrierRoot"]) / job / "native-process-started.ready.json")
    require(ready.get("operationId") == job and ready.get("barrier") == "native-process-started"
            and isinstance(ready.get("workerId"), str) and TOKEN.fullmatch(ready["workerId"]), "barrier-identity")
    require(type(ready.get("workerProcessId")) is int and ready["workerProcessId"] > 0
            and type(ready.get("childProcessId")) is int and ready["childProcessId"] > 0
            and isinstance(ready.get("readyAt"), str), "barrier-process-identity")
    ready_at = datetime.fromisoformat(ready["readyAt"].replace("Z", "+00:00"))
    require(ready_at.tzinfo is not None and 0 <= (datetime.now(timezone.utc) - ready_at).total_seconds()
            <= MAX_BARRIER_AGE_SECONDS, "barrier-stale")
    result["barrier"] = {key: ready[key] for key in (
        "operationId", "workerId", "workerProcessId", "childProcessId", "readyAt")}
    entry_path = Path(value["barrierRoot"]) / job / "native-process-started.entry.json"
    entry, entry_digest = read_json(entry_path)
    require(entry.get("schema") == "honua.gp-native-entry/v1"
            and entry.get("boundary") == "native-exec-entry-paused"
            and type(entry.get("pid")) is int and entry["pid"] == ready["childProcessId"]
            and type(entry.get("parentPid")) is int and entry["parentPid"] == ready["workerProcessId"]
            and isinstance(entry.get("nonce"), str) and re.fullmatch(r"[0-9a-f]{32}", entry["nonce"]),
            "native-entry-identity")
    executables = {str(Path(path).resolve(strict=True)) for path in NATIVE_EXECUTABLES if Path(path).exists()}
    require(entry.get("executable") in executables, "native-entry-executable")
    require(0 <= time.time() - entry_path.stat().st_mtime <= MAX_BARRIER_AGE_SECONDS
            and entry_path.stat().st_mtime <= ready_at.timestamp() + 1, "native-entry-stale")
    # Preserve the process identity keyset while binding BOTH immutable receipts.
    # sha256 is SHA256(ASCII(readySha256 + "\n" + nativeEntrySha256)).
    bound_digest = hashlib.sha256((barrier_digest + "\n" + entry_digest).encode("ascii")).hexdigest()
    result["barrier"].update({"sha256": bound_digest, "readySha256": barrier_digest,
                            "nativeEntrySha256": entry_digest, "nativeExecutable": entry["executable"],
                            "nativeEntryBoundary": entry["boundary"]})
    if phase == "started":
        require(read_json(entry_path)[1] == entry_digest
                and read_json(Path(value["barrierRoot"]) / job / "native-process-started.ready.json")[1]
                == barrier_digest, "native-entry-observation-race")
        # This is independent filesystem evidence of native entry, NOT a claim
        # that a PID is currently alive. Batch must also observe its exact task
        # RUNNING before cancel and STOPPED afterwards through provider APIs.
        return result
    require(value["role"] == "worker", "worker-policy-required")
    worker, child = process(ready.get("workerProcessId")), process(ready.get("childProcessId"))
    require(worker is not None, "worker-not-observable")
    boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
    namespace = os.stat("/proc/self/ns/pid").st_ino
    identity = {"workerId": ready["workerId"], "worker": worker, "bootId": boot,
                "pidNamespace": namespace, "barrierSha256": bound_digest}
    if phase == "before":
        require(child is not None and child["parentPid"] == worker["pid"], "native-child-not-running")
        require(str(Path(f"/proc/{child['pid']}/exe").resolve(strict=True)) == entry["executable"],
                "native-entry-executable-mismatch")
        require(child["startTicks"] >= worker["startTicks"], "native-child-identity")
        cwd = Path(f"/proc/{child['pid']}/cwd").resolve(strict=True)
        root = Path(value["scratchRoot"])
        require(cwd != root and cwd.is_relative_to(root), "native-scratch-scope")
        relative = cwd.relative_to(root).as_posix()
        require(all(TOKEN.fullmatch(x) for x in relative.split("/")), "native-scratch-scope")
        identity.update({"child": child, "scratchRelative": relative})
        result["process"] = identity
        result["scratchInventory"] = inventory(root, relative)
    else:
        require(isinstance(expected, dict) and set(expected) == {
            "workerId", "worker", "child", "bootId", "pidNamespace", "barrierSha256", "scratchRelative"},
            "expected-identity")
        require(all(identity[k] == expected[k] for k in identity), "worker-identity-changed")
        require(type(expected["child"]) is dict and expected["child"].get("pid") == ready.get("childProcessId"),
                "child-identity-changed")
        require(child is None, "native-child-still-present")
        result["process"] = {**expected, "childAbsent": True}
        result["scratchInventory"] = inventory(value["scratchRoot"], expected["scratchRelative"])
    require(process(worker["pid"]) == worker and process(ready["childProcessId"]) == child,
            "process-observation-race")
    require(read_json(Path(value["barrierRoot"]) / job / "native-process-started.ready.json")[1]
            == barrier_digest, "barrier-observation-race")
    require(read_json(entry_path)[1] == entry_digest, "native-entry-observation-race")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--policy", default="/run/honua/observer-policy/observer-policy.json")
    parser.add_argument("--job", required=True)
    parser.add_argument("--nonce", required=True)
    parser.add_argument("--phase", choices=("before", "after", "started", "store"), required=True)
    parser.add_argument("--expected", help="base64url encoded retained before-process identity")
    args = parser.parse_args()
    try:
        # ECS Exec launches as root even though this image's worker runs 1001.
        # Read /proc with the worker UID, without granting SYS_PTRACE or keeping
        # root privileges. No policy or job-controlled input selects this UID.
        if os.geteuid() == 0:
            os.setgroups([])
            os.setgid(1001)
            os.setuid(1001)
        require(os.getresuid() == (1001, 1001, 1001) and os.getresgid() == (1001, 1001, 1001),
                "native-image-user-required")
        expected = json.loads(base64.urlsafe_b64decode(args.expected)) if args.expected else None
        result = observe(args.policy, args.job, args.nonce, args.phase, expected)
    except (Refused, OSError, ValueError, TypeError, KeyError, OverflowError):
        # Do not serialize system exceptions, file contents, environments or commands.
        print(json.dumps({"schema": SCHEMA, "status": "blocked", "qualification": False,
                          "jobId": args.job, "nonce": args.nonce, "phase": args.phase,
                          "reason": "native-observation-refused"}))
        return 2
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
