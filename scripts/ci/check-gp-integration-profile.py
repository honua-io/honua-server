#!/usr/bin/env python3
"""Check that docker-compose.gp-dev.yml defines the integration profile.

When Docker Compose is available, the rendered config from
`docker compose -f docker-compose.gp-dev.yml --profile integration config`
is the source of truth. The compose file is also checked directly so the
profile, worker services, artifact volume, and process-source mount cannot
disappear in an environment without a daemon. This script is not a CI gate.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[2]
DEFAULT_COMPOSE = REPO / "docker-compose.gp-dev.yml"
PROFILE = "integration"
WORKERS = ("managed-worker", "gdal-worker")
ARTIFACT_VOLUME = "gp_artifacts"
ARTIFACT_TARGET = "/var/lib/honua/gp-artifacts"
SOURCE_TARGET = "/workspace/process-source"


def split_short_volume(item):
    """Split Compose short syntax without breaking on ``${VAR:-default}``."""
    if not isinstance(item, str):
        return None
    read_only = item.endswith(":ro")
    text = item[:-3] if read_only else item
    for target in (ARTIFACT_TARGET, SOURCE_TARGET):
        suffix = ":" + target
        if text.endswith(suffix):
            return text[: -len(suffix)], target, read_only
    return None


def has_artifact_mount(volumes):
    for item in volumes or []:
        if isinstance(item, str):
            parsed = split_short_volume(item)
            if parsed is None:
                continue
            source, target, _read_only = parsed
            if target == ARTIFACT_TARGET and (source == ARTIFACT_VOLUME or source.endswith("_" + ARTIFACT_VOLUME)):
                return True
        elif isinstance(item, dict) and item.get("target") == ARTIFACT_TARGET:
            source = str(item.get("source") or "")
            kind = item.get("type")
            if kind in (None, "volume") and (source == ARTIFACT_VOLUME or source.endswith("_" + ARTIFACT_VOLUME)):
                return True
    return False


def has_source_mount(volumes):
    for item in volumes or []:
        if isinstance(item, str):
            parsed = split_short_volume(item)
            if parsed is None:
                continue
            _source, target, read_only = parsed
            if target == SOURCE_TARGET and read_only:
                return True
        elif isinstance(item, dict) and item.get("target") == SOURCE_TARGET:
            read_only = item.get("read_only")
            kind = item.get("type")
            if kind in (None, "bind") and read_only in (True, "true"):
                return True
    return False


def source_problems(document):
    """Problems in the compose file itself, before profile selection."""
    problems = []
    if not isinstance(document, dict):
        return ["compose file is not a mapping"]
    services = document.get("services")
    volumes = document.get("volumes")
    if not isinstance(services, dict):
        problems.append("missing services mapping")
        services = {}
    if not isinstance(volumes, dict) or ARTIFACT_VOLUME not in volumes:
        problems.append(f"missing volume {ARTIFACT_VOLUME}")
    for name in WORKERS:
        service = services.get(name)
        if not isinstance(service, dict):
            problems.append(f"missing service {name}")
            continue
        profiles = service.get("profiles") or []
        if PROFILE not in profiles:
            problems.append(f"{name} is not in profile {PROFILE}")
        mounts = service.get("volumes") or []
        if not has_artifact_mount(mounts):
            problems.append(f"{name} does not mount {ARTIFACT_VOLUME} at {ARTIFACT_TARGET}")
        if not has_source_mount(mounts):
            problems.append(f"{name} does not bind-mount process source at {SOURCE_TARGET}")
    return problems


def rendered_problems(config, *, expect_workers):
    """Problems in `docker compose config` JSON."""
    problems = []
    services = config.get("services") if isinstance(config, dict) else None
    if not isinstance(services, dict):
        return ["compose config has no services"]
    volumes = config.get("volumes") if isinstance(config.get("volumes"), dict) else {}
    for name in WORKERS:
        present = name in services
        if expect_workers and not present:
            problems.append(f"profile {PROFILE} did not select {name}")
        if not expect_workers and present:
            problems.append(f"{name} is active without profile {PROFILE}")
        if not present:
            continue
        mounts = services[name].get("volumes") or []
        if expect_workers and not has_artifact_mount(mounts):
            problems.append(f"rendered {name} lost the artifact volume")
        if expect_workers and not has_source_mount(mounts):
            problems.append(f"rendered {name} lost the process source mount")
    if expect_workers and ARTIFACT_VOLUME not in volumes:
        problems.append(f"rendered config lost volume {ARTIFACT_VOLUME}")
    return problems


def compose_config(compose_file, profile=None):
    command = ["docker", "compose", "-f", str(compose_file), "--project-directory", str(compose_file.parent)]
    if profile:
        command.extend(["--profile", profile])
    command.extend(["config", "--format", "json"])
    completed = subprocess.run(command, check=False, capture_output=True, text=True)
    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout or "docker compose config failed").strip()
        raise RuntimeError(detail)
    return json.loads(completed.stdout)


def docker_available():
    return shutil.which("docker") is not None


def check(compose_file, *, use_docker):
    document = yaml.safe_load(compose_file.read_text(encoding="utf-8"))
    problems = [f"source: {problem}" for problem in source_problems(document)]
    if use_docker:
        if not docker_available():
            problems.append("docker was required but is not on PATH")
        else:
            try:
                base = compose_config(compose_file)
                selected = compose_config(compose_file, PROFILE)
            except (RuntimeError, json.JSONDecodeError, OSError) as exc:
                problems.append(f"docker compose config: {exc}")
            else:
                problems.extend(f"default config: {problem}" for problem in rendered_problems(base, expect_workers=False))
                problems.extend(
                    f"integration config: {problem}" for problem in rendered_problems(selected, expect_workers=True))
    return problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--file", type=Path, default=DEFAULT_COMPOSE)
    parser.add_argument("--no-docker", action="store_true", help="check the compose file only")
    args = parser.parse_args(argv)
    compose_file = args.file.resolve()
    use_docker = not args.no_docker
    problems = check(compose_file, use_docker=use_docker)
    if problems:
        print("GP integration profile check failed:", file=sys.stderr)
        for problem in problems:
            print(f"- {problem}", file=sys.stderr)
        return 1
    if use_docker and docker_available():
        print(f"GP integration profile matches docker compose --profile {PROFILE} config")
    else:
        print("GP integration profile matches the compose file (docker compose config skipped)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
