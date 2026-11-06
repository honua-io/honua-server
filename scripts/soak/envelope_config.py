#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Derive the soak server's configuration from the frozen envelope and refuse a drifted server.

The envelope declares ``gpWorkers`` executing jobs and ``gpQueueDepth`` queued jobs. The server's
``ExecutionAdmission:MaxConcurrentJobsGlobal`` counts every *active* job, queued and executing
alike (``ExecutionAdmissionEvaluator`` reads ``IJobStore.ListActiveAsync``), so the limit that admits
the declared queue is ``gpWorkers + gpQueueDepth``. Setting it to ``gpWorkers`` alone caps the queue at
zero: candidate run 40542363841 refused 108,842 submissions with "Global active job limit reached
(1/1)" and never held more than one job.

Execution concurrency is not an admission setting. Each server replica runs one sequential
``JobExecutionService`` claim loop, so the soak's single replica provides exactly one worker.

``services`` counts the published data services. The catalogue root also lists the built-in
``Utilities/PrintingTools`` GPServer and the public geocode locator; neither is a published service,
and counting them reported three services for a one-service seed.

Run as a script, this reads the container's effective environment and the served catalogue and exits
non-zero when either differs from the envelope, before any observation is collected.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import urllib.request
from pathlib import Path
from typing import Any

CONTAINER = "honua-capacity-soak-honua-1"
SERVICE = "test"
# Service types that publish data. GPServer and GeocodeServer entries are also listed by the
# catalogue for built-ins, so a name counts only when it publishes one of these.
DATA_SERVICE_TYPES = frozenset({"FeatureServer", "MapServer", "ImageServer", "SceneServer", "VectorTileServer"})
# One sequential JobExecutionService claim loop per server replica.
WORKERS_PER_REPLICA = 1


def admission_active_job_limit(envelope: dict[str, Any]) -> int:
    """Active (queued + executing) job limit that admits exactly the declared GP envelope."""
    return int(envelope["gpWorkers"]) + int(envelope["gpQueueDepth"])


def expected_settings(envelope: dict[str, Any]) -> dict[str, str]:
    active = str(admission_active_job_limit(envelope))
    return {
        "ExecutionAdmission__Enabled": "true",
        "ExecutionAdmission__MaxConcurrentJobsGlobal": active,
        "ExecutionAdmission__MaxConcurrentJobsPerPartition": active,
        # The soak's single principal submits continuously; the per-principal rate gate is not
        # part of the declared envelope.
        "ExecutionAdmission__MaxSubmissionsPerWindow": "0",
        # A single-host load generator behind the opt-in app limiter measures the throttle.
        "RateLimiting__Enabled": "false",
    }


def data_services(catalog: dict[str, Any]) -> list[str]:
    """Distinct published data-service names in a ``/rest/services`` document."""
    return sorted({entry["name"] for entry in catalog.get("services", []) if entry.get("type") in DATA_SERVICE_TYPES})


def configured_gp_workers(replicas: int) -> int:
    return replicas * WORKERS_PER_REPLICA


def check(envelope: dict[str, Any], environment: dict[str, str], catalog: dict[str, Any], layers: int,
          replicas: int) -> list[str]:
    """Every difference between the started server and the envelope; empty when they agree."""
    failures = []
    for key, expected in expected_settings(envelope).items():
        actual = environment.get(key)
        if actual != expected:
            failures.append(f"{key} is {actual!r}, envelope requires {expected!r}")
    workers = configured_gp_workers(replicas)
    if workers != envelope["gpWorkers"]:
        failures.append(f"gpWorkers: {replicas} replica(s) provide {workers} worker(s), envelope declares {envelope['gpWorkers']}")
    services = data_services(catalog)
    if len(services) != envelope["services"]:
        failures.append(f"services: catalogue publishes {services}, envelope declares {envelope['services']}")
    if layers != envelope["layersPerService"]:
        failures.append(f"layersPerService: service serves {layers} layers, envelope declares {envelope['layersPerService']}")
    return failures


def container_environment(container: str) -> dict[str, str]:
    inspection = json.loads(subprocess.check_output(["docker", "inspect", container], text=True))[0]
    return dict(item.split("=", 1) for item in inspection["Config"]["Env"] if "=" in item)


def running_replicas(project: str) -> int:
    output = subprocess.check_output(
        ["docker", "ps", "--quiet", "--filter", f"label=com.docker.compose.project={project}",
         "--filter", "label=com.docker.compose.service=honua"], text=True)
    return len(output.split())


def get_json(base_url: str, path: str, admin_key: str) -> Any:
    request = urllib.request.Request(base_url.rstrip("/") + path, headers={"X-API-Key": admin_key})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--admin-key", required=True)
    parser.add_argument("--container", default=CONTAINER)
    parser.add_argument("--project", default="honua-capacity-soak")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    envelope = json.loads(args.lock.read_bytes())["supportedEnvelope"]
    environment = container_environment(args.container)
    catalog = get_json(args.base_url, "/rest/services?f=json", args.admin_key)
    layers = len(get_json(args.base_url, f"/rest/services/{SERVICE}/FeatureServer?f=json", args.admin_key)["layers"])
    replicas = running_replicas(args.project)
    failures = check(envelope, environment, catalog, layers, replicas)
    report = dict(status="fail" if failures else "pass", failures=failures,
                  effective={key: environment.get(key) for key in expected_settings(envelope)},
                  dataServices=data_services(catalog), layersPerService=layers, replicas=replicas,
                  gpWorkers=configured_gp_workers(replicas))
    if args.report:
        args.report.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
    if failures:
        for failure in failures:
            print(f"::error::soak server differs from the frozen envelope: {failure}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
