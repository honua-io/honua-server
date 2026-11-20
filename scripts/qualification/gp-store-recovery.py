#!/usr/bin/env python3
"""Cold-backup and restore the isolated GP qualification topology, including staged bytes.

Never use this destructive drill against a customer deployment. The caller owns the
unique Compose project and bind-mounted output directory. All application writers and
PostgreSQL are stopped before the physical backup, and no original volume survives.
The servers' shared data-protection key ring is restored with the database, because the
job records in it are sealed with that ring.
"""

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

KEY_RING = "/tmp/honua/.aspnet/DataProtection-Keys"


def run(*args, **kwargs):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, **kwargs).stdout


def now():
    return datetime.now(timezone.utc).isoformat()


def owned_volume(compose, project, destination, hosts):
    """Names the one project-owned named volume every host mounts at destination."""
    names = set()
    for host in hosts:
        container = run(*compose, "ps", "-q", host).decode().strip()
        details = json.loads(run("docker", "inspect", container))[0]
        mounts = [m for m in details["Mounts"] if m["Destination"] == destination]
        if len(mounts) != 1 or mounts[0]["Type"] != "volume":
            raise ValueError(f"{host} must mount one named persistent volume at {destination}")
        names.add(mounts[0]["Name"])
    if len(names) != 1:
        raise ValueError(f"every host must share one volume at {destination}")
    volume = names.pop()
    labels = json.loads(run("docker", "volume", "inspect", volume))[0]["Labels"]
    if labels.get("com.docker.compose.project") != project:
        raise ValueError("refusing a volume not owned by this qualification project")
    return volume


def recover(compose_file, project, object_root, backup_dir):
    if not project.startswith("honua-gp-reliability-"):
        raise ValueError("recovery is restricted to isolated GP qualification projects")
    object_root = object_root.resolve(strict=True)
    backup_dir = backup_dir.resolve()
    if object_root == backup_dir or object_root in backup_dir.parents:
        raise ValueError("backup directory must be outside the volume being destroyed")
    marker = object_root / ".honua-gp-store.json"
    attestation = json.loads(marker.read_text())
    if attestation["StoreReference"] != "qualification":
        raise ValueError("only the qualification store can be destroyed")
    backup_dir.mkdir(parents=True, exist_ok=False)
    compose = ["docker", "compose", "--project-name", project, "-f", str(compose_file)]
    for host in ("server", "server-peer", "worker"):
        container = run(*compose, "ps", "-q", host).decode().strip()
        details = json.loads(run("docker", "inspect", container))[0]
        mounts = [m for m in details["Mounts"] if m["Destination"] == "/var/lib/honua/gp-outputs"]
        if (len(mounts) != 1 or mounts[0]["Type"] != "bind"
                or Path(mounts[0]["Source"]).resolve() != object_root):
            raise ValueError("output directory must be the actual shared mount of every qualification host")
    database = owned_volume(compose, project, "/var/lib/postgresql/data", ("postgres",))
    key_ring = owned_volume(compose, project, KEY_RING, ("server", "server-peer"))
    output_store = {"store": "gp-output", "volume": str(object_root), "type": "bind"}
    inventory = [{"store": "postgres", "volume": database, "type": "volume"},
                 {"store": "data-protection-keys", "volume": key_ring, "type": "volume"}, output_store]
    helper = json.loads(run(*compose, "config", "--format", "json"))["services"]["postgres"]["image"]

    def mounted(item, *command, **kwargs):
        mount = f'type={item["type"]},src={item["volume"]},dst=/volume'
        return run("docker", "run", "--rm", "--user", "0:0", "--mount", mount,
                   "--entrypoint", "sh", helper, "-c", *command, **kwargs)

    def file_inventory(item):
        return mounted(item, "cd /volume && find . -type f -exec sha256sum {} \\; | LC_ALL=C sort").decode()

    started = now()
    run(*compose, "stop", "server", "server-peer", "worker")
    run(*compose, "stop", "postgres")
    for item in inventory:
        item["files_before"] = file_inventory(item)
        archive = mounted(item, "tar -C /volume -cpf - .")
        path = backup_dir / (item["store"] + ".tar")
        path.write_bytes(archive)
        item.update(backup=path.name, sha256=hashlib.sha256(archive).hexdigest(),
                    bytes=len(archive), captured_at=now())
    # No subsequent read can fall back to an original database or output object.
    run(*compose, "down", "--volumes", "--remove-orphans")
    for item in inventory:
        if item["type"] != "volume":
            continue
        result = subprocess.run(["docker", "volume", "inspect", item["volume"]],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if result.returncode == 0:
            raise RuntimeError(f'original {item["store"]} volume survived destruction')
    mounted(output_store, "find /volume -mindepth 1 -delete")
    destroyed = now()
    # Creating the primary server also recreates the key-ring volume it shares with the peer.
    run(*compose, "create", "postgres", "server")
    for item in inventory:
        if mounted(item, "find /volume -mindepth 1 -print -quit").strip():
            raise RuntimeError(f'{item["store"]}: restore destination is not empty')
        archive = (backup_dir / item["backup"]).read_bytes()
        if hashlib.sha256(archive).hexdigest() != item["sha256"]:
            raise RuntimeError("backup checksum changed before restore")
        # -i is needed to pass the saved archive to tar inside the helper container.
        mount = f'type={item["type"]},src={item["volume"]},dst=/volume'
        run("docker", "run", "--rm", "-i", "--user", "0:0", "--mount", mount,
            "--entrypoint", "tar", helper, "-C", "/volume", "-xpf", "-", input=archive)
        item["files_after"] = file_inventory(item)
        if item["files_before"] != item["files_after"]:
            raise RuntimeError(f'{item["store"]}: restored file inventory differs')
        item.update(original_destroyed=True, restored_into_empty_store=True, restored_at=now())
    run(*compose, "up", "-d")
    return {"started_at": started, "destroyed_at": destroyed, "restored_at": now(),
            "attestation": attestation, "substrates": inventory}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compose-file", type=Path, required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument("--object-root", type=Path, required=True)
    parser.add_argument("--backup-dir", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()
    receipt = recover(args.compose_file, args.project, args.object_root, args.backup_dir)
    args.receipt.write_text(json.dumps(receipt, indent=2) + "\n")
