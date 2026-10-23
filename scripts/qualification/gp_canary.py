#!/usr/bin/env python3
"""Candidate-bound scheduled GP smoke evidence, not whole-catalog qualification."""
import base64
import hashlib
import importlib.util
import json
import math
import os
import re
import struct
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCHEMA = "honua.gp-canary.v2"
FIXTURE = "projected-buffer-dissolve-4096.v1"
PROCESSES = ("geometry.buffer", "geometry.dissolve")
TOLERANCE = 1e-6


def now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def gh_json(*arguments):
    return json.loads(subprocess.run(["gh", *arguments], capture_output=True, check=True).stdout)


def fetch_manifest(release_ref, path):
    token = os.environ.get("HONUA_GP_CANARY_RELEASE_TOKEN")
    if not token:
        raise ValueError("RELEASE_BUNDLE_TOKEN with honua-release contents:read access is missing")
    result = subprocess.run(
        ["gh", "api", "--method", "GET", "repos/honua-io/honua-release/contents/platform-manifest.yaml",
         "-H", "Accept: application/vnd.github.raw+json", "-f", f"ref={release_ref}"],
        env={**os.environ, "GH_TOKEN": token}, capture_output=True, timeout=30, check=False)
    if result.returncode:
        raise ValueError("RELEASE_BUNDLE_TOKEN could not read the frozen private release manifest")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(result.stdout)


def scheduled_slot(created):
    created = timestamp(created)
    slot = created.replace(hour=(created.hour // 6) * 6, minute=17, second=0, microsecond=0)
    if created < slot:
        slot -= timedelta(hours=6)
    if not timedelta(0) <= created - slot <= timedelta(minutes=90):
        raise ValueError("scheduled run is outside the 90-minute start window")
    return slot.isoformat().replace("+00:00", "Z")


def payload(process):
    if process == PROCESSES[0]:
        point = base64.b64encode(struct.pack("<BIdd", 1, 1, 1000, 2000)).decode()
        inputs = {"wkb": point, "srid": 3857, "distance": 100}
    else:
        # 4 groups x 32x32 overlapping 2m squares. Each independently covers a
        # 33x33 rectangle. The oracle never dissolves the submitted geometries.
        wkbs, keys = [], []
        for group in range(4):
            for x in range(32):
                for y in range(32):
                    left = group * 100 + x
                    ring = [(left, y), (left + 2, y), (left + 2, y + 2), (left, y + 2), (left, y)]
                    raw = struct.pack("<BIII", 1, 3, 1, len(ring))
                    raw += b"".join(struct.pack("<dd", *point) for point in ring)
                    wkbs.append(base64.b64encode(raw).decode())
                    keys.append(str(group))
        inputs = {"wkbs": wkbs, "srid": 3857, "groupKeys": keys}
    return {"response": "document", "inputs": inputs}


def oracle(process, document):
    # GEOS validates topology; expected geometry is analytical construction,
    # never a buffer/union calculated by the implementation under test.
    from shapely.geometry import Polygon, box, shape
    if process == PROCESSES[0]:
        if document.get("type") != "Feature":
            raise ValueError("buffer must produce one Feature")
        features = [document]
        expected = {None: Polygon([(1000 + 100 * math.cos(i * math.pi / 16),
                                   2000 + 100 * math.sin(i * math.pi / 16)) for i in range(32)])}
        expected_area = 16 * 10000 * math.sin(math.pi / 16)
    else:
        if (document.get("type") != "FeatureCollection" or document.get("inputSrid") != 3857
                or document.get("inputCount") != 4096 or document.get("groupCount") != 4
                or document.get("processId") != process):
            raise ValueError("dissolve collection metadata/count/CRS mismatch")
        features = document.get("features", [])
        expected = {str(group): box(group * 100, 0, group * 100 + 33, 33) for group in range(4)}
        expected_area = 1089
    if len(features) != len(expected):
        raise ValueError("feature count mismatch")
    measured, seen = [], set()
    for feature in features:
        props = feature.get("properties", {})
        key = props.get("groupKey")
        if key not in expected or key in seen:
            raise ValueError("missing, duplicate or unexpected group")
        seen.add(key)
        if feature.get("type") != "Feature" or props.get("processId") != process or props.get("inputSrid") != 3857:
            raise ValueError("feature process/CRS metadata mismatch")
        if process == PROCESSES[0] and props.get("bufferDistance") != 100:
            raise ValueError("buffer distance metadata mismatch")
        geometry = shape(feature["geometry"])
        target = expected[key]
        if geometry.geom_type != "Polygon" or geometry.is_empty or not geometry.is_valid:
            raise ValueError("output polygon topology is invalid")
        difference = geometry.symmetric_difference(target).area
        envelope_error = max(abs(a - b) for a, b in zip(geometry.bounds, target.bounds))
        if not all(math.isfinite(metric) and metric <= TOLERANCE
                   for metric in (difference, envelope_error, abs(geometry.area - expected_area))):
            raise ValueError("numerical area/envelope/shape oracle mismatch")
        measured.append({"group": key, "area": geometry.area, "expected_area": expected_area,
                         "envelope": list(geometry.bounds), "expected_envelope": list(target.bounds),
                         "symmetric_difference_area": difference, "valid": True})
    return {"fixture": FIXTURE, "feature_count": len(features), "srid": 3857,
            "absolute_tolerance": TOLERANCE, "features": sorted(measured, key=lambda row: str(row["group"]))}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Client:
    def __init__(self, endpoint, token):
        self.endpoint, self.token = endpoint.rstrip("/"), token
        self.opener = urllib.request.build_opener(NoRedirect)

    def request(self, path, body=None, expected=200):
        if path.startswith("//"):
            raise ValueError("cross-origin artifact reference is not permitted")
        url = urllib.parse.urljoin(self.endpoint + "/", path)
        target, origin = urllib.parse.urlsplit(url), urllib.parse.urlsplit(self.endpoint)
        if (target.scheme, target.netloc) != (origin.scheme, origin.netloc):
            raise ValueError("cross-origin artifact reference is not permitted")
        headers = {"Authorization": "Bearer " + self.token, "Accept": "application/json"}
        if body is not None:
            headers.update({"Content-Type": "application/json", "Prefer": "respond-async"})
        try:
            with self.opener.open(urllib.request.Request(url, data=body, headers=headers), timeout=30) as response:
                if response.status != expected:
                    raise ValueError(f"unexpected HTTP status {response.status}")
                raw = response.read(8 * 1024 * 1024 + 1)
                if len(raw) > 8 * 1024 * 1024:
                    raise ValueError("response exceeds canary evidence limit")
                return json.loads(raw)
        except urllib.error.HTTPError as error:
            raise ValueError(f"HTTP request failed with status {error.code}") from None

    def identity(self, candidate):
        server = self.request("api/v1/capabilities/manifest").get("server", {})
        if (server.get("deploymentRevisionSource") != "image-digest"
                or server.get("deploymentRevision") != candidate["server_digest"]):
            raise ValueError("live deployment image digest does not match frozen candidate")
        return {"deploymentRevision": server["deploymentRevision"],
                "deploymentRevisionSource": server["deploymentRevisionSource"]}


def resolve_output(client, result):
    if result.get("type") in ("Feature", "FeatureCollection"):
        return result
    outputs = list(result.values())
    if len(outputs) != 1 or not isinstance(outputs[0], dict):
        raise ValueError("expected exactly one operation output")
    output = outputs[0]
    if "value" in output:
        return output["value"]
    href = output.get("href", "")
    if href.startswith(("data:application/geo+json;base64,", "data:application/json;base64,")):
        return json.loads(base64.b64decode(href.split(",", 1)[1], validate=True))
    if href and (href.startswith("/") or href.startswith(client.endpoint + "/")):
        return client.request(href)
    raise ValueError("missing or unsupported result artifact reference")


def run_operation(client, process, folder, receipt):
    operation = {"process": process, "fixture": FIXTURE, "outcome": "fail", "started_at": now(), "transitions": []}
    receipt["operations"].append(operation)
    started = time.monotonic()
    try:
        name = process.replace(".", "-")
        input_path, output_path = folder / f"{name}-input.json", folder / f"{name}-output.json"
        write_json(input_path, payload(process))
        operation["input"] = {"file": input_path.name, "sha256": sha(input_path.read_bytes())}
        submit = client.request(f"ogc/processes/processes/{process}/execution", input_path.read_bytes(), 201)
        job = submit.get("jobID")
        if not isinstance(job, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", job):
            raise ValueError("submission did not return a valid operation ID")
        operation.update(operation_id=job, submit_http_status=201)
        while time.monotonic() - started < 180:
            status = client.request(f"ogc/processes/jobs/{job}").get("status")
            operation["transitions"].append({"at": now(), "status": status})
            if status == "successful":
                break
            if status not in ("accepted", "running"):
                raise ValueError("operation reached an unsuccessful or unknown state")
            time.sleep(2)
        else:
            raise ValueError("operation exceeded the 180-second canary deadline")
        output = resolve_output(client, client.request(f"ogc/processes/jobs/{job}/results"))
        write_json(output_path, output)
        operation["output"] = {"file": output_path.name, "sha256": sha(output_path.read_bytes())}
        operation["metrics"] = oracle(process, output)
        operation["outcome"] = "pass"
    except Exception as error:
        operation["finding"] = str(error)
    finally:
        operation.update(completed_at=now(), latency_seconds=time.monotonic() - started)


def main():
    receipt_path = Path(os.environ.get("HONUA_GP_CANARY_RECEIPT", "artifacts/gp-canary/receipt.json"))
    receipt = {"schema": SCHEMA, "fixture": FIXTURE, "outcome": "fail", "started_at": now(), "operations": []}
    try:
        endpoint = os.environ.get("HONUA_GP_CANARY_URL", "").rstrip("/")
        parsed = urllib.parse.urlsplit(endpoint)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ValueError("HONUA_GP_CANARY_URL must be a credential-free HTTPS deployment URL")
        receipt["endpoint"] = endpoint
        token = os.environ.get("HONUA_GP_CANARY_TOKEN")
        if not token:
            raise ValueError("HONUA_GP_CANARY_TOKEN is missing")
        release_ref = os.environ.get("HONUA_GP_CANARY_RELEASE_REF", "")
        if not re.fullmatch(r"[a-f0-9]{40}", release_ref):
            raise ValueError("frozen honua-release commit is missing or mutable")
        spec = importlib.util.spec_from_file_location("binding", Path(__file__).with_name("gp-candidate-binding.py"))
        binding = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(binding)
        manifest_path = Path(os.environ["HONUA_GP_CANARY_MANIFEST"])
        fetch_manifest(release_ref, manifest_path)
        candidate = binding.candidate(manifest_path)
        candidate.update(release_ref=release_ref, harness_sha=os.environ.get("GITHUB_SHA", ""))
        candidate["oracle_sha256"] = sha(Path(__file__).read_bytes())
        if not re.fullmatch(r"[a-f0-9]{40}", candidate["harness_sha"]):
            raise ValueError("immutable harness SHA is missing")
        receipt["candidate"] = candidate
        run = gh_json("api", f"repos/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}")
        receipt["github"] = {key: run[key] for key in ("id", "run_attempt", "event", "created_at", "head_sha", "html_url")}
        if run["event"] == "schedule" and run["run_attempt"] == 1:
            receipt["scheduled_slot"] = scheduled_slot(run["created_at"])
        client = Client(endpoint, token)
        receipt["identity_before"] = client.identity(candidate)
        for process in PROCESSES:
            run_operation(client, process, receipt_path.parent, receipt)
        receipt["identity_after"] = client.identity(candidate)
        if not all(operation["outcome"] == "pass" for operation in receipt["operations"]):
            raise ValueError("one or more required operation oracles failed")
        receipt["outcome"] = "pass"
    except Exception as error:
        receipt["finding"] = str(error)
    finally:
        receipt["completed_at"] = now()
        write_json(receipt_path, receipt)
    return 0 if receipt["outcome"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
