#!/usr/bin/env python3
"""Private lane driver. AWS responses and credentials stay in a private temporary directory."""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from urllib.parse import urlencode, urlsplit

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/seed/client-compat-v1.sql"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def fingerprint(value):
    return "sha256:" + hashlib.sha256(value.encode()).hexdigest()


def aws(*args):
    result = subprocess.run(["aws", *args, "--output", "json"], capture_output=True, text=True)
    # Never echo CLI diagnostics: configuration responses can contain credentials.
    require(result.returncode == 0, f"AWS {args[0]} {args[1]} failed")
    return json.loads(result.stdout or "{}")


def write_json(path, data):
    Path(path).write_text(json.dumps(data))


def config(function, qualifier=None):
    args = ["lambda", "get-function", "--function-name", function]
    if qualifier:
        args += ["--qualifier", qualifier]
    return aws(*args)


def inputs():
    function = os.environ["REALAWS_CERT_LAMBDA_FUNCTION"]
    alias = os.environ["REALAWS_CERT_LAMBDA_ALIAS"]
    require(re.fullmatch(r"honua-cert-cert-[A-Za-z0-9_-]+", function), "Standing function outside cert namespace")
    require(re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", alias), "Invalid cert alias")
    write = urlsplit(os.environ["HONUA_LAMBDA_WRITE_BASE_URL"])
    demo = urlsplit(os.environ["HONUA_DEMO_BASE_URL"])
    for url in (write, demo):
        require(url.scheme == "https" and url.hostname and not url.username and not url.password
                and not url.query and not url.fragment and url.path in ("", "/"), "Expected an HTTPS base URL")
    require(write.hostname.lower() != demo.hostname.lower(), "REFUSED: write base URL equals demo read URL host")
    actual = aws("lambda", "get-function-url-config", "--function-name", function, "--qualifier", alias)
    require(actual["FunctionUrl"].rstrip("/").lower() == os.environ["HONUA_LAMBDA_WRITE_BASE_URL"].rstrip("/").lower(),
            "Write URL does not belong to the standing cert alias")
    return function, alias


def prepare(directory):
    function, alias = inputs()
    current = config(function)
    cfg = current["Configuration"]
    require(cfg["PackageType"] == "Image", "Standing function must use an image")
    require(cfg["Architectures"] == [os.environ["HONUA_LAMBDA_ARCHITECTURE"]], "Standing architecture mismatch")
    variables = cfg["Environment"]["Variables"]
    require(variables.get("HONUA_SKIP_MIGRATIONS", "false").lower() == "false", "Standing function skips migrations: noProof")
    require(variables.get("ConnectionStrings__DefaultConnection"), "Cert PostGIS connection is missing")
    require(cfg["VpcConfig"].get("SubnetIds") and cfg["VpcConfig"].get("SecurityGroupIds"), "Cert PostGIS VPC is missing")
    # The standing function already reaches the cert stack's private PostGIS and resolves its secrets.
    # Clone its configuration, including authentication; never substitute a loopback connection.
    write_json(directory / "environment.json", {"Variables": variables})
    write_json(directory / "vpc.json", {k: cfg["VpcConfig"][k] for k in ("SubnetIds", "SecurityGroupIds")})
    write_json(directory / "standing.json", current)


def admin_key():
    # Runtime-only secret; not written to a receipt, stdout, or a repository path.
    return os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"]


def invoke(function, path, *, method="GET", query=None, body=None, authenticated=True, expected_version=None):
    headers = {"accept": "application/json", "host": urlsplit(os.environ["HONUA_LAMBDA_WRITE_BASE_URL"]).netloc}
    if authenticated:
        headers["x-api-key"] = admin_key()
    if body is not None:
        headers["content-type"] = "application/x-www-form-urlencoded"
    event = {"version": "2.0", "routeKey": f"{method} {path}", "rawPath": path,
             "rawQueryString": urlencode(query or {}), "headers": headers,
             "requestContext": {"http": {"method": method, "path": path, "protocol": "HTTP/1.1",
                                         "sourceIp": "127.0.0.1", "userAgent": "honua-lambda-cert"}},
             "isBase64Encoded": False}
    if body is not None:
        event["body"] = urlencode(body)
    with tempfile.TemporaryDirectory(prefix="honua-cert-invoke-") as temporary:
        payload, response = Path(temporary) / "payload.json", Path(temporary) / "response.json"
        write_json(payload, event)
        meta = aws("lambda", "invoke", "--function-name", function, "--cli-binary-format", "raw-in-base64-out",
                   "--log-type", "Tail", "--payload", f"file://{payload}", str(response))
        require(meta.get("StatusCode") == 200 and not meta.get("FunctionError"), "Lambda invocation failed")
        if expected_version:
            require(meta.get("ExecutedVersion") == expected_version, "Alias invocation executed the wrong version")
        result = json.loads(response.read_text())
        raw = result.get("body", "")
        if result.get("isBase64Encoded"):
            raw = base64.b64decode(raw).decode()
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError:
            parsed = raw
        return result.get("statusCode"), parsed, meta


def ok(function, path, **kwargs):
    status, body, _ = invoke(function, path, **kwargs)
    require(status == 200 and isinstance(body, dict) and "error" not in body, "Serving HTTP assertion failed")
    return body


def smoke(function, expected_version=None):
    common = {"expected_version": expected_version}
    migration = ok(function, "/api/v1/admin/observability/migrations", **common)
    require(migration.get("status") == "succeeded" and migration.get("isReady") is True
            and migration.get("isFailed") is False and migration.get("planAvailable") is True
            and migration.get("upgradeRequired") is False and migration.get("pendingScripts") == [],
            "Migrations not applied: noProof")
    path = "/rest/services/test_service/FeatureServer/0"
    # Fixed, versioned fixture; never infer the expected count from the target's answer.
    count = ok(function, path + "/query", query={"f": "json", "where": "1=1", "returnCountOnly": "true"}, **common)
    require(type(count.get("count")) is int and count["count"] == 10, "Fixture row count must equal 10")
    rows = ok(function, path + "/query", query={"f": "json", "where": "1=1", "outFields": "name", "returnGeometry": "false"}, **common)
    expected_names = sorted(["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "lambda"])
    require(sorted(feature["attributes"]["name"] for feature in rows.get("features", [])) == expected_names,
            "Fixture records do not match client-compat-v1")
    # The anonymous principal has no admin rights. This documented 401 must contain no records.
    status, denial, _ = invoke(function, "/api/v1/admin/api-keys", authenticated=False, **common)
    require(status == 401 and isinstance(denial, dict), "Authorization denial must be HTTP 401")
    require(denial.get("status") == 401 and denial.get("type") == "https://honua.io/problems/admin",
            "Authorization refusal body is not the documented error")
    require(not any(key in denial for key in ("data", "features", "records", "layers", "items", "apiKeys")),
            "Authorization denial leaked records")
    write_path = "/rest/services/test_service/FeatureServer/10"
    marker = "honua-certrun-" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]
    # Resolve by our unique marker during finally too, including ambiguous create responses.
    query = {"f": "json", "where": f"name = '{marker}'", "outFields": "*", "returnGeometry": "false"}
    require(ok(function, write_path + "/query", query=query, **common).get("features") == [], "Write marker already exists")
    cleanup_needed = True
    try:
        added = ok(function, write_path + "/addFeatures", method="POST", body={"f": "json", "features": json.dumps([
            {"attributes": {"name": marker}, "geometry": {"x": -122.42, "y": 37.76, "spatialReference": {"wkid": 4326}}}])}, **common)
        results = added.get("addResults", [])
        require(len(results) == 1 and results[0].get("success") is True and type(results[0].get("objectId")) is int,
                "Create assertion failed")
        object_id = results[0]["objectId"]
        read = ok(function, write_path + "/query", query=query, **common).get("features", [])
        require(len(read) == 1 and read[0]["attributes"].get("name") == marker
                and read[0]["attributes"].get("objectid") == object_id, "Write readback assertion failed")
        deleted = ok(function, write_path + "/deleteFeatures", method="POST", body={"f": "json", "objectIds": str(object_id)}, **common)
        results = deleted.get("deleteResults", [])
        require(len(results) == 1 and results[0].get("success") is True and results[0].get("objectId") == object_id,
                "Delete assertion failed")
        require(ok(function, write_path + "/query", query=query, **common).get("features") == [], "Deleted row still served")
        cleanup_needed = False
    finally:
        if cleanup_needed:
            # Only this run's marker; never a broad fixture reset or delete by untrusted ID.
            result = ok(function, write_path + "/deleteFeatures", method="POST", body={"f": "json", "where": query["where"]}, **common)
            require(all(item.get("success") is True for item in result.get("deleteResults", [])), "Write cleanup failed")
            require(ok(function, write_path + "/query", query=query, **common).get("features") == [], "Write cleanup left records")
    return {"result": "pass", "migrations": {"status": "succeeded", "pendingScripts": 0, "upgradeRequired": False},
            "fixture": {"name": "client-compat-v1", "sha256": fingerprint(FIXTURE.read_text()), "expectedRows": 10, "actualRows": 10, "namesVerified": True},
            "write": {"createdRows": 1, "readBackRows": 1, "deletedRows": 1, "remainingRows": 0, "distinctWriteUrl": True},
            "authorization": {"principal": "anonymous", "operation": "GET /api/v1/admin/api-keys", "expectedStatus": 401, "actualStatus": 401, "records": 0},
            "executedVersion": expected_version or "$LATEST"}


def backend(action, function, alias, previous, candidate):
    driver = ROOT / "scripts/cloud/lambda-deploy-driver/bin/Release/net10.0/LambdaDeployDriver.dll"
    result = subprocess.run(["dotnet", str(driver), action, function, alias, previous, candidate, os.environ["AWS_REGION"]],
                            capture_output=True, text=True)
    require(result.returncode == 0, f"Deploy backend {action} failed")
    value = json.loads(result.stdout)
    require(value.get("version") == (previous if action == "rollback" else candidate), "Backend version assertion failed")


def alias_state(function, alias, expected=None):
    state = aws("lambda", "get-alias", "--function-name", function, "--name", alias)
    require(not state.get("RoutingConfig", {}).get("AdditionalVersionWeights"), "Standing alias has weighted traffic")
    require(re.fullmatch(r"[1-9][0-9]*", state.get("FunctionVersion", "")), "Standing alias needs a published version")
    if expected:
        require(state["FunctionVersion"] == expected, "Alias version assertion failed")
    return state["FunctionVersion"]


def certify(directory, ephemeral, digest):
    function, alias = inputs()
    proof = {"result": "noProof", "candidateDigest": digest.split("@")[-1]}
    write_json(directory / "serving.json", proof)
    proof["deployed"] = smoke(ephemeral)
    previous = alias_state(function, alias)
    target = function + ":" + alias
    # Prove the baseline can serve before publishing anything to the standing function.
    proof["baseline"] = smoke(target, previous)
    original = json.loads((directory / "standing.json").read_text())
    latest = config(function)
    require(latest["Configuration"]["RevisionId"] == original["Configuration"]["RevisionId"], "Standing function changed during certification")
    candidate = None
    cleanup_errors = []

    def clean(action):
        try:
            action()
        except Exception as error:
            cleanup_errors.append(error)

    changed = False
    rollback_needed = False
    try:
        changed = True
        update = aws("lambda", "update-function-code", "--function-name", function, "--image-uri", digest,
                     "--revision-id", latest["Configuration"]["RevisionId"])
        aws("lambda", "wait", "function-updated-v2", "--function-name", function)
        deployed = config(function)
        require(deployed["Code"]["ResolvedImageUri"] == digest, "Standing candidate digest mismatch")
        published = aws("lambda", "publish-version", "--function-name", function,
                        "--revision-id", deployed["Configuration"]["RevisionId"],
                        "--code-sha256", update["CodeSha256"], "--description", "honua-cert-run=" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"])
        candidate = published["Version"]
        require(re.fullmatch(r"[1-9][0-9]*", candidate) and candidate != previous, "Candidate is not a new published version")
        require(config(function, candidate)["Code"]["ResolvedImageUri"] == digest, "Published candidate digest mismatch")
        rollback_needed = True  # Set BEFORE the call: an SDK timeout may follow a successful shift.
        backend("shift", function, alias, previous, candidate)
        alias_state(function, alias, candidate)
        proof["candidate"] = smoke(target, candidate)
    finally:
        if rollback_needed:
            clean(lambda: backend("rollback", function, alias, previous, candidate))
            clean(lambda: alias_state(function, alias, previous))
            def verify_rollback():
                proof["rollback"] = smoke(target, previous)
            clean(verify_rollback)
        if changed:
            def restore_latest():
                # Restore pre-existing $LATEST code as well as alias routing. Configuration was never edited.
                current = config(function)
                require(current["Code"]["ResolvedImageUri"] in (digest, original["Code"]["ResolvedImageUri"]),
                        "Standing latest drifted; refusing to overwrite unrelated code")
                aws("lambda", "update-function-code", "--function-name", function,
                    "--image-uri", original["Code"]["ResolvedImageUri"], "--revision-id", current["Configuration"]["RevisionId"])
                aws("lambda", "wait", "function-updated-v2", "--function-name", function)
                require(config(function)["Code"]["ResolvedImageUri"] == original["Code"]["ResolvedImageUri"], "Standing latest restoration failed")
            clean(restore_latest)
        if candidate:
            def delete_candidate():
                alias_state(function, alias, previous)
                aliases = aws("lambda", "list-aliases", "--function-name", function)["Aliases"]
                require(all(a["FunctionVersion"] != candidate and candidate not in a.get("RoutingConfig", {}).get("AdditionalVersionWeights", {}) for a in aliases),
                        "Candidate still referenced; refusing version deletion")
                aws("lambda", "delete-function", "--function-name", function, "--qualifier", candidate)
                versions = aws("lambda", "list-versions-by-function", "--function-name", function)["Versions"]
                require(all(v["Version"] != candidate for v in versions), "Candidate version remains after deletion")
            clean(delete_candidate)
    require(not cleanup_errors, "Alias rollback or candidate teardown failed")
    proof.update(result="pass", alias={"beforeVersion": previous, "afterVersion": candidate, "rollbackVersion": previous},
                 teardown={"candidateVersionDeleted": True, "standingLatestRestored": True})
    write_json(directory / "serving.json", proof)


if __name__ == "__main__":
    try:
        if sys.argv[1] == "prepare":
            prepare(Path(sys.argv[2]))
        elif sys.argv[1] == "certify":
            certify(Path(sys.argv[2]), sys.argv[3], sys.argv[4])
        else:
            raise RuntimeError("Unknown lane command")
    except (RuntimeError, KeyError, ValueError, OSError):
        # Raw API exceptions/bodies can contain secrets. Receipts remain noProof on any error.
        print("Lambda certification assertion failed; serving noProof", file=sys.stderr)
        sys.exit(1)
