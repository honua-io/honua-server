"""Execute the WFS semantic oracle through an already-running candidate's workflow API.

The caller supplies a TLS fixture certificate trusted by the candidate. No product
HTTP handler, executor, persistence, or certificate validation is substituted.
Run only against a disposable qualification deployment: this creates workflow packages.
"""

import argparse
import base64
import copy
import hashlib
import json
import os
import ssl
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


ROWS = [
    (11, "survey:points", True, [1, 2, 3.25], "Kīlauea 日本"),
    (12, "survey:points", True, [3, 4, -1.5], None),
    (13, "survey:points", True, [5, 6, 9.75], "last"),
    (21, "other:points", True, [1, 2, 3], "wrong type"),
    (22, "survey:points", False, [1, 2, 3], "inactive"),
    (23, "survey:points", True, [50, 60, 3], "outside"),
]


def assert_features(document):
    assert document["type"] == "FeatureCollection"
    assert document["processId"] == "source.wfs"
    assert document["featureCount"] == 3
    features = document["features"]
    assert len(features) == 3
    # Independent literals; never derive expected content from the returned artifact.
    for feature, key, xyz, name, serial in zip(features, [11, 12, 13],
            [[1, 2, 3.25], [3, 4, -1.5], [5, 6, 9.75]],
            ["Kīlauea 日本", None, "last"],
            [9007199254741004, 9007199254741005, 9007199254741006]):
        assert feature["geometry"] == {"type": "Point", "coordinates": xyz}
        assert feature["properties"] == {"key": key, "serial": serial, "name": name, "active": True}


def utc():
    return datetime.now(timezone.utc).isoformat()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--container", required=True)
    parser.add_argument("--image", required=True, help="Manifest-pinned image@sha256:digest")
    parser.add_argument("--fixture-address", required=True)
    parser.add_argument("--fixture-port", type=int, default=18450)
    parser.add_argument("--certificate", required=True)
    parser.add_argument("--observations-url", required=True)
    parser.add_argument("--receipt", required=True)
    args = parser.parse_args()
    receipt = {"schema": "honua.wfs-candidate-proof.v1", "startedAt": utc(),
               "outcome": "fail", "requestedImage": args.image, "scenarios": [],
               "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    try:
        observed = json.loads(subprocess.check_output(["docker", "inspect", args.container]))[0]
        image = json.loads(subprocess.check_output(["docker", "image", "inspect", observed["Image"]]))[0]
        assert "@sha256:" in args.image
        assert args.image == observed["Config"]["Image"]
        assert args.image in image["RepoDigests"]
        receipt["candidate"] = {"containerId": observed["Id"], "imageId": observed["Image"],
            "repoDigests": image["RepoDigests"], "labels": image["Config"].get("Labels"),
            "memoryLimitBytes": observed["HostConfig"]["Memory"], "nanoCpus": observed["HostConfig"]["NanoCpus"]}

        def api(path, body=None):
            request = urllib.request.Request(args.base_url.rstrip("/") + path,
                data=None if body is None else json.dumps(body).encode(),
                headers={"Content-Type": "application/json", "X-API-Key": os.environ["HONUA_GP_ADMIN_KEY"]})
            try:
                with urllib.request.urlopen(request, timeout=15) as response:
                    value = json.load(response)
                    return value.get("data", value)
            except urllib.error.HTTPError as error:
                raise AssertionError(f"{request.method} {path}: {error.code} {error.read().decode()}") from error

        for matched in [False, True]:
            package = "wfs-proof-" + uuid.uuid4().hex[:12]
            mode = "matched" if matched else "unknown"
            scenario = {"numberMatched": matched, "packageId": package, "startedAt": utc(), "states": []}
            receipt["scenarios"].append(scenario)
            root = "/api/v1/console/workflow-packages/" + package
            api("/api/v1/console/workflow-packages", {"packageId": package, "name": package,
                "namespace": "qualification", "graph": {"nodes": [{"nodeId": "wfs",
                    "nodeTypeId": "process:source.wfs", "parameters": {
                        "serviceUrl": f"https://{args.fixture_address}:{args.fixture_port}/wfs/{mode}/{package}",
                        "typeName": "survey:points", "where": "active = true", "bbox": "0,0,10,10", "pageSize": "2"}}],
                    "edges": []}})
            version = api(root + "/versions", {})
            assert version["validation"]["isValid"] is True
            api(root + "/versions/1/publish", {"publicationId": package, "target": "Schedule", "enabled": True,
                "schedule": {"cronExpression": "0 0 1 1 *", "timeZone": "UTC", "enabled": False}})
            run = api("/api/v1/console/workflow-publications/" + package + "/runs", {})
            scenario["workflowRunId"] = run["workflowRunId"]
            deadline = time.monotonic() + 150
            while time.monotonic() < deadline:
                jobs = api("/api/v1/admin/jobs?limit=100")["items"]
                jobs = [j for j in jobs if j.get("definitionId") == f"workflow-package:{package}:v1:wfs"]
                if jobs:
                    job = jobs[0]
                    if not scenario["states"] or scenario["states"][-1] != job["status"]:
                        scenario["states"].append(job["status"])
                    if job["status"] in ["Succeeded", "Failed", "Cancelled", "TimedOut"]:
                        break
                time.sleep(1)
            else:
                raise AssertionError(f"workflow did not terminate: {scenario}")
            scenario["job"] = job
            assert job["status"] == "Succeeded", job
            artifacts = api(f'/api/v1/admin/jobs/{job["jobId"]}/artifacts')
            scenario["artifacts"] = artifacts
            refs = [a["artifactId"] for a in artifacts["items"]]
            refs = [r for r in refs if r.startswith("data:application/geo+json;base64,")]
            assert len(refs) == 1, artifacts
            data = base64.b64decode(refs[0].split(",", 1)[1], validate=True)
            document = json.loads(data)
            assert_features(document)
            wrong = copy.deepcopy(document)
            wrong["features"][2] = copy.deepcopy(wrong["features"][1])
            try:
                assert_features(wrong)
            except AssertionError:
                scenario["duplicatePageRejected"] = True
            else:
                raise AssertionError("oracle accepted a plausible duplicated page")
            with urllib.request.urlopen(args.observations_url + "/" + package,
                    context=ssl.create_default_context(cafile=args.certificate)) as response:
                observations = json.load(response)
            assert not observations["errors"], observations
            starts = observations["starts"]
            assert starts == ([0, 1, 2] if matched else [0, 1, 2, 3]), starts
            scenario.update(outcome="pass", starts=list(starts), outputBytes=len(data),
                outputSha256=hashlib.sha256(data).hexdigest(), completedAt=utc())
        receipt["semanticOutcome"] = "pass"
        for scenario in receipt["scenarios"]:
            deadline = time.monotonic() + 30
            while True:
                workflow = api("/api/v1/admin/operations/" + scenario["workflowRunId"])
                scenario["workflow"] = workflow
                if workflow.get("CompletedAt") or time.monotonic() >= deadline:
                    break
                time.sleep(1)
            scenario["workflowOutcome"] = "pass" if workflow.get("CurrentPhase") == "Succeeded" else "fail"
        assert all(s["workflowOutcome"] == "pass" for s in receipt["scenarios"]), "Child content passed, but parent workflow did not succeed"
        receipt["outcome"] = "pass"
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        receipt["completedAt"] = utc()
        Path(args.receipt).write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
