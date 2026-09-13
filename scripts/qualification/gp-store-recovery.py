#!/usr/bin/env python3
"""Cold-backup and restore the isolated GP qualification topology, including staged bytes.

Never use this destructive drill against a customer deployment. The caller owns the
unique Compose project and bind-mounted output directory. All application writers and
both databases are stopped before the physical backup, and no original volume survives.
"""

import argparse
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path


def run(*args, **kwargs):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, **kwargs).stdout


def now():
    return datetime.now(timezone.utc).isoformat()


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
    inventory = []
    for service, target in (("postgres", "/var/lib/postgresql/data"), ("redis", "/data")):
        container = run(*compose, "ps", "-q", service).decode().strip()
        details = json.loads(run("docker", "inspect", container))[0]
        mounts = [m for m in details["Mounts"] if m["Destination"] == target]
        if len(mounts) != 1 or mounts[0]["Type"] != "volume":
            raise ValueError(f"{service} must use one named persistent volume")
        volume = mounts[0]["Name"]
        labels = json.loads(run("docker", "volume", "inspect", volume))[0]["Labels"]
        if labels.get("com.docker.compose.project") != project:
            raise ValueError("refusing a volume not owned by this qualification project")
        inventory.append({"store": service, "volume": volume, "type": "volume"})
    inventory.append({"store": "gp-output", "volume": str(object_root), "type": "bind"})
    helper = json.loads(run(*compose, "config", "--format", "json"))["services"]["postgres"]["image"]

    def mounted(item, *command, **kwargs):
        mount = f'type={item["type"]},src={item["volume"]},dst=/volume'
        return run("docker", "run", "--rm", "--user", "0:0", "--mount", mount,
                   "--entrypoint", "sh", helper, "-c", *command, **kwargs)

    def file_inventory(item):
        return mounted(item, "cd /volume && find . -type f -exec sha256sum {} \\; | LC_ALL=C sort").decode()

    started = now()
    run(*compose, "stop", "server", "server-peer", "worker")
    run(*compose, "stop", "postgres", "redis")
    for item in inventory:
        item["files_before"] = file_inventory(item)
        archive = mounted(item, "tar -C /volume -cpf - .")
        path = backup_dir / (item["store"] + ".tar")
        path.write_bytes(archive)
        item.update(backup=path.name, sha256=hashlib.sha256(archive).hexdigest(),
                    bytes=len(archive), captured_at=now())
    # No subsequent read can fall back to an original database or output object.
    run(*compose, "down", "--volumes", "--remove-orphans")
    for item in inventory[:2]:
        result = subprocess.run(["docker", "volume", "inspect", item["volume"]],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if result.returncode == 0:
            raise RuntimeError("original database volume survived destruction")
    mounted(inventory[2], "find /volume -mindepth 1 -delete")
    destroyed = now()
    run(*compose, "create", "postgres", "redis")
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
