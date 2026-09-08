"""Offline execution of the complete lane with stateful AWS CLI/container/backend doubles."""
from pathlib import Path
import hashlib
import importlib.util
import io
import json
import os
import subprocess
import tempfile
import unittest
import unittest.mock

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = ROOT / "scripts/cloud/certify-lambda-preview.sh"
SCRIPT = SCRIPT_PATH.read_text()
WORKFLOW = (ROOT / ".github/workflows/lambda-preview-certification.yml").read_text()

# One executable, symlinked as aws/crane/docker/dotnet/sleep. Unknown calls fail rather than succeed.
STUB = r'''#!/usr/bin/env python3
import base64, fcntl, json, os, sys
from pathlib import Path
from urllib.parse import parse_qs
args = sys.argv[1:]
name = Path(sys.argv[0]).name
path = Path(os.environ["STUB_STATE"])
if name == "docker" and args[0] == "login":
    sys.stdin.read()
lock = open(str(path) + ".lock", "w")
fcntl.flock(lock, fcntl.LOCK_EX)
s = json.loads(path.read_text())
fail = os.environ.get("STUB_FAIL", "")
def arg(key, default=None):
    return args[args.index(key)+1] if key in args else default
def emit(value):
    path.write_text(json.dumps(s))
    if value is not None:
        print(value if isinstance(value, str) else json.dumps(value))
    sys.exit(0)
def bad(message=None, code=1):
    path.write_text(json.dumps(s))
    if message:
        print(message, file=sys.stderr)
    sys.exit(code)
s["calls"].append([name] + [x for x in args if not x.startswith("file://")])
digest = "sha256:" + "b"*64
repo = os.environ["HONUA_LAMBDA_PREVIEW_REPOSITORY"]
manifest = {"config": {"digest": "sha256:" + "c"*64}, "layers": [{"digest":"sha256:"+"d"*64}]}
rootfs = ["sha256:"+"1"*64, "sha256:"+"2"*64]
child = "sha256:" + "9"*64
index_mode = os.environ.get("STUB_INDEX", "")
arch = "arm64" if os.environ["HONUA_LAMBDA_ARCHITECTURE"] == "arm64" else "amd64"
if name == "sleep": emit(None)
if name == "crane":
    if args[0] != "copy": bad()
    if s["ecr"] is not None:
        # The certification repository is tag-immutable: the manifest PUT is rejected outright.
        print("PUT ...: TAG_INVALID: The image tag '%s' already exists and cannot be overwritten "
              "because the repository is immutable" % args[2].rsplit(":", 1)[-1], file=sys.stderr)
        bad()
    # crane uploads the manifest and its blobs verbatim, so ECR keeps the exact config blob and rootfs.
    s["mirrored"] = args[1]
    s["ecr"] = {"digest": digest, "config": manifest["config"]["digest"],
                "layers": [layer["digest"] for layer in manifest["layers"]], "rootfs": rootfs}
    emit(None)
if name == "docker":
    if args[:3] == ["buildx", "imagetools", "inspect"]:
        if index_mode and not args[-1].endswith(child):
            children = [{"digest": child, "platform": {"os": "linux", "architecture": arch}},
                        {"digest": "sha256:"+"8"*64, "platform": {"os": "linux", "architecture": "ppc64le"}},
                        {"digest": "sha256:"+"7"*64, "platform": {"os": "unknown", "architecture": "unknown"}}]
            if index_mode == "no-match":
                children = [c for c in children if c["platform"]["architecture"] != arch]
            if index_mode == "ambiguous":
                children.append({"digest": "sha256:"+"6"*64, "platform": {"os": "linux", "architecture": arch}})
            emit({"mediaType": "application/vnd.oci.image.index.v1+json", "manifests": children})
        emit(manifest)
    if args[:2] == ["image", "inspect"]:
        mirrored = ".dkr.ecr." in args[2]
        if "RootFS" in args[-1]:
            emit(["sha256:"+"0"*64] if mirrored and fail == "rootfs" else rootfs)
        if "Architecture" in args[-1]: emit("wrong" if fail == "architecture" or fail == "ecr-platform" and mirrored else arch)
        emit("e"*40 if fail == "revision" else os.environ["HONUA_LAMBDA_SERVER_REVISION"])
    if args[0] == "run" and fail == "adapter": bad()
    if args[0] in ("pull", "run", "login"): emit(None)
    bad()
if name == "dotnet":
    action, previous, candidate = args[1], args[4], args[5]
    s["backend"].append(action)
    if fail == "backend-rollback-unapplied" and action == "rollback": bad()
    s["alias"] = previous if action == "rollback" else candidate
    if action == "shift": s["shifted"] = True
    if action == "rollback": s["rolledback"] = True
    if fail == "backend-shift" and action == "shift": bad() # applied, response lost
    if fail == "backend-rollback" and action == "rollback": bad()
    emit({"version": s["alias"], "status": "RolledBack" if action == "rollback" else "Succeeded"})
if name != "aws": bad()
service, op = args[:2]
if service == "secretsmanager":
    assert op == "get-secret-value"
    if fail == "secret-access-denied":
        print("An error occurred (AccessDeniedException): offline-sensitive-canary", file=sys.stderr)
        bad()
    if fail == "secret-read-failed":
        print("offline-sensitive-canary", file=sys.stderr)
        bad()
    emit({"SecretString": "" if fail == "secret-empty" else "offline-sensitive-canary"})
function = arg("--function-name", "")
if service == "sts": emit("123456789012")
if service == "ecr":
    if op == "get-login-password": emit("offline-password")
    if op == "describe-images":
        if fail == "describe-error":
            print("An error occurred (AccessDeniedException) when calling the DescribeImages "
                  "operation: not authorized", file=sys.stderr)
            bad()
        if not s["ecr"]:
            print("An error occurred (ImageNotFoundException) when calling the DescribeImages "
                  "operation: The image with imageId {imageTag: %s} does not exist"
                  % arg("--image-ids", ""), file=sys.stderr)
            bad()
        if arg("--query"): emit("bad" if fail == "digest" else s["ecr"]["digest"])
        emit({"imageDetails": [{"imageDigest": s["ecr"]["digest"]}]})
    if op == "batch-delete-image":
        assert arg("--repository-name").endswith("honua-cert-cert-lambda-preview")
        assert arg("--image-ids").startswith("imageTag=candidate-")
        if fail == "stale-delete":
            emit({"imageIds": [], "failures": [{"failureCode": "ImageNotFound"}]})
        s["deleted_tags"].append(arg("--image-ids"))
        s["ecr"] = None
        emit({"imageIds": [{"imageTag": arg("--image-ids").split("=", 1)[1]}], "failures": []})
    if op == "batch-get-image":
        if not s["ecr"] or fail == "manifest-error":
            if fail == "manifest-error":
                print("An error occurred (AccessDeniedException) when calling the BatchGetImage "
                      "operation: not authorized", file=sys.stderr)
            bad()
        stored = {"config": {"digest": s["ecr"]["config"]},
                  "layers": [{"digest": d} for d in s["ecr"]["layers"]]}
        if fail == "mirror": stored["config"]["digest"] = "sha256:"+"f"*64
        if fail == "layers": stored["layers"] = []
        emit(stored)
    bad()
if service == "logs":
    if op == "describe-log-groups": emit("1" if s["logs"] else "0")
    if op == "create-log-group": s["logs"] = True; emit(None)
    if op == "put-retention-policy": emit(None)
    if op == "delete-log-group":
        if fail != "log-delete": s["logs"] = False
        emit(None)
    if op == "filter-log-events":
        # The real CLI paginates and prints one count per page; the pass path
        # must survive a multi-page answer, and the query must be bounded.
        assert "--start-time" in args, "filter-log-events must be bounded by --start-time"
        if arg("--query") == "events[0].logStreamName":
            # One stream is one execution environment; delivery names it before it carries the
            # platform lines, and answers None until the request id itself has been delivered.
            emit(s["stream"] or "None")
        if "events[].message" in args:
            # Platform lines read back from CloudWatch when the invoke tail lacks them. The group
            # holds one stream per execution environment this run forced, so the query must name
            # the stream its invoke ran in: an unscoped read would see every other attempt too.
            pattern = args[args.index("--filter-pattern") + 1]
            assert "--log-stream-names" in args, "cold-start evidence must be scoped to one stream"
            stream = arg("--log-stream-names")
            s["stream_scoped"] = True
            s["stream_queries"].append(stream)
            s["platform_queries"] += 1
            # Delivery of the platform lines lags the request id by minutes.
            delivered = s["platform_queries"] > int(os.environ.get("STUB_CLOUDWATCH_LAG", "0"))
            if (os.environ.get("STUB_CLOUDWATCH_INIT") == "invoke" and pattern == "INIT_REPORT"
                    and delivered and stream in s["cold_streams"]):
                emit(["INIT_REPORT Init Duration: 21364.18 ms\tPhase: invoke\tStatus: ok"])
            emit([])
        emit("0\n0" if fail == "cloudwatch" else "0\n0\n1\n0")
    bad()
if service != "lambda": bad()
if op == "get-function-url-config": emit({"FunctionUrl": "https://cert.lambda-url.us-east-1.on.aws/"})
if op == "wait": emit(None)
if op == "get-function":
    ephemeral = function.startswith("honua-certrun-")
    if ephemeral and not s["function"]:
        # GetFunction reports a missing function as ResourceNotFoundException, and a throttle or a
        # service outage as something else entirely, over the same nonzero CLI exit.
        if fail == "get-function-transient":
            bad("An error occurred (TooManyRequestsException) when calling the GetFunction operation "
                "(reached max retries: 2): Rate exceeded", 254)
        bad("An error occurred (ResourceNotFoundException) when calling the GetFunction operation: "
            "Function not found: arn:offline:ephemeral", 254)
    image = repo + "@" + digest if ephemeral else s["image"]
    if arg("--qualifier") == "8": image = repo + "@" + digest
    if ephemeral and fail == "resolved-image": image = "wrong"
    query = arg("--query")
    if query == "Code.ResolvedImageUri": emit(image)
    if query == "Configuration.FunctionArn": emit("arn:offline:ephemeral")
    variables = {"ConnectionStrings__DefaultConnection": "aws:secretsmanager:offline-db",
                 "ConnectionStrings__redis": "aws:secretsmanager:offline-redis",
                 "HONUA_ADMIN_PASSWORD": os.environ.get("STUB_ADMIN_REFERENCE", "aws:secretsmanager:offline-admin"), "HONUA_SKIP_MIGRATIONS": "false",
                 # The scratch-layer write is a Pro surface, so a certifiable standing function
                 # carries a signed license envelope and the key that verifies its signature.
                 "Licensing__LicenseContentSecretRef": "aws:secretsmanager:offline-license",
                 "Licensing__TrustedKeys__honuademo2026q2": "base64url:offline-trusted-public-key"}
    if fail == "unlicensed-edits":
        variables.pop("Licensing__LicenseContentSecretRef")
        variables.pop("Licensing__TrustedKeys__honuademo2026q2")
    if fail == "missing-redis" or fail == "alias-missing-redis" and arg("--qualifier") == "live":
        variables.pop("ConnectionStrings__redis")
    if fail == "skip-config": variables["HONUA_SKIP_MIGRATIONS"] = "true"
    if fail == "missing-db": variables.pop("ConnectionStrings__DefaultConnection")
    if fail == "missing-admin-key": variables.pop("HONUA_ADMIN_PASSWORD")
    # Whitespace is not a credential: ResolveAdminPasswordAsync treats it as unconfigured.
    if fail == "blank-admin-key": variables["HONUA_ADMIN_PASSWORD"] = "   "
    # The deployed function lost the credential the standing configuration still carries, which is
    # what a published version with its own frozen environment, or a republish, can leave behind.
    if fail == "admin-unconfigured" and ephemeral: variables.pop("HONUA_ADMIN_PASSWORD")
    vpc = {"SubnetIds": [], "SecurityGroupIds": []} if fail == "missing-vpc" else {"SubnetIds":["subnet-cert"], "SecurityGroupIds":["sg-cert"]}
    emit({"Configuration": {"RevisionId": "rev", "Description": "honua-cert-run=123-1" if arg("--qualifier") == "8" else "standing", "PackageType": "Image", "Architectures": [os.environ["HONUA_LAMBDA_ARCHITECTURE"]],
         "FunctionArn": "arn:aws:lambda:us-east-1:123456789012:function:honua-cert-cert-server",
         "Environment": {"Variables": variables}, "VpcConfig": vpc},
         "Code": {"ResolvedImageUri": image}})
if op == "create-function":
    env = json.loads(Path(arg("--environment")[7:]).read_text())
    assert env["Variables"]["HONUA_SKIP_MIGRATIONS"] == "false"
    assert arg("--vpc-config").startswith("file://")
    # Load the paramfile the way the CLI would. Asserting only the argument shape cannot tell a
    # written VPC config from an argument naming a file the lane never produced, which is the
    # difference between reaching the cert PostGIS and a client-side create failure.
    vpc = json.loads(Path(arg("--vpc-config")[7:]).read_text())
    assert vpc["SubnetIds"] and vpc["SecurityGroupIds"]
    s["vpc"] = vpc
    assert arg("--architectures") == os.environ["HONUA_LAMBDA_ARCHITECTURE"]
    if fail == "create-error":
        # A service error that quotes the lane's own inputs back at it, as Lambda validation does.
        path.write_text(json.dumps(s))
        print("An error occurred (InvalidParameterValueException) when calling the CreateFunction "
              "operation: The role arn:aws:iam::123456789012:role/cert cannot reach "
              + env["Variables"]["ConnectionStrings__DefaultConnection"], file=sys.stderr)
        sys.exit(254)
    s["function"] = True
    emit({"FunctionArn":"arn:offline:ephemeral"})
if op == "update-function-configuration":
    # The nonce discards every execution environment the function holds. It must never displace
    # the cloned standing configuration, and it must differ on every attempt.
    env = json.loads(Path(arg("--environment")[7:]).read_text())
    assert env["Variables"]["HONUA_SKIP_MIGRATIONS"] == "false"
    assert env["Variables"]["ConnectionStrings__DefaultConnection"]
    nonce = env["Variables"]["HONUA_LAMBDA_CERT_COLD_START"]
    assert nonce.startswith(os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"] + "-")
    s["environments"] += 1
    s["nonces"].append(nonce)
    emit({"FunctionArn": "arn:offline:ephemeral", "LastUpdateStatus": "InProgress"})
if op == "list-tags": emit({"honua-cert-run": "wrong" if fail == "ownership" else "123-1"})
if op == "delete-function":
    if arg("--qualifier"):
        assert arg("--qualifier") == "8" and s["alias"] == "7"
        s["deleted_versions"].append("8")
        if fail != "version-delete": s["versions"] = ["7"]
    elif fail != "function-delete": s["function"] = False
    emit(None)
if op == "get-alias": emit({"FunctionVersion": s["alias"], "RoutingConfig": {"AdditionalVersionWeights": {"6": 0.1} if fail == "weighted" else {}}})
if op == "update-function-code": s["image"] = arg("--image-uri"); emit({"CodeSha256":"code"})
if op == "publish-version":
    s["versions"].append("8")
    if fail == "publish-response-lost": bad()
    emit({"Version":"8"})
if op == "list-aliases": emit({"Aliases":[{"FunctionVersion":s["alias"]}]})
if op == "list-versions-by-function": emit({"Versions":[{"Version":v,"Description":"honua-cert-run=123-1" if v == "8" else "standing"} for v in s["versions"]]})
def geoservices_entitlement_refusal(fail):
    # GeoServices answers a refused operation with HTTP 200 and the whole failure in the envelope.
    # This is run 28's answer verbatim: FeatureServer editing is gated on the Pro entitlement
    # `editing.featureserver-edits`, so an unlicensed function refuses the run-owned write and
    # nothing about the fixture or the payload is wrong.
    detail = ("FeatureServer Editing requires an active Pro entitlement. Current edition is "
              "Community; install a license that includes 'editing.featureserver-edits'."
              if fail == "unlicensed-edits"
              else "A valid paid license is required. Renew the configured license.")
    return {"error":{"code":402, "message":"Payment Required",
                     "details":[detail, "entitlement: editing.featureserver-edits",
                                "Timestamp: 2026-09-08T00:00:00.000Z"]}}


if op != "invoke": bad()
payload = arg("--payload")
event = json.loads(Path(payload[7:]).read_text() if payload.startswith("file://") else payload)
# The shell's invoke has no --output json; Python's helper adds it after the response path.
response_path = Path(args[args.index("--payload")+2])
route = event["rawPath"]
if route not in ("/healthz/live", "/api/v1/admin/api-keys"):
    assert event["headers"]["x-api-key"] == (os.environ.get("HONUA_LAMBDA_CERT_ADMIN_KEY") or "offline-sensitive-canary")
status = 200
body = {}
headers = None
version = s["alias"] if ":" in function else "$LATEST"
phase = "rollback" if s["rolledback"] else "candidate" if s["shifted"] else "deployed" if function.startswith("honua-certrun-") else "baseline"
if fail == "candidate-query" and phase == "candidate": fail = "query"
if fail == "rollback-query" and phase == "rollback": fail = "query"
if route == "/healthz/live":
    body = "Unwell" if fail == "health-body" else "Healthy"
    if fail == "health-status": status = 500
elif route.endswith("/migrations"):
    body = {"status":"succeeded", "isReady":True, "isFailed":False, "planAvailable":True, "upgradeRequired":False, "pendingScripts":[]}
    if fail in ("admin-401", "admin-unconfigured", "admin-unresolvable"):
        # What an administrative refusal actually looks like: one problem document whose title is
        # "Unauthorized" whichever cause produced it, and the challenge the handler appends. Payload
        # format 2.0 folds the two WWW-Authenticate values the server writes into a single header.
        # "admin-unresolvable" is the third cause: the variable is there, but the handler could not
        # turn the reference into a usable password, so it answers exactly as it does for an absent one.
        status = 401
        body = {"type":"https://honua.io/problems/admin", "title":"Unauthorized", "status":401,
                "detail":"API key required. Provide a valid API key in the X-API-Key header."
                         if fail == "admin-401" else "Admin authentication not configured"}
        headers = {"content-type":"application/problem+json",
                   "www-authenticate":'ApiKey realm="Honua Admin", header="X-API-Key", Basic realm="Honua Admin", charset="UTF-8"'}
    if fail == "license-blocked":
        # LicenseOperationMiddleware refuses the whole deployment rather than one gated surface:
        # every route outside /healthz and the license/auth admin routes answers HTTP 402 before it
        # reaches a handler, as a problem document and not a GeoServices envelope. The lane meets
        # this on its FIRST serving assertion, long before any FeatureServer edit.
        status = 402
        body = {"type":"https://honua.io/problems/admin", "title":"Payment Required", "status":402,
                "detail":"License unavailable or expired. Renew the configured license; "
                         "re-validation runs every minute, or restart."}
        headers = {"content-type":"application/problem+json"}
    if fail == "migrations": body["status"] = "skipped"
    if fail == "migration-pending": body["pendingScripts"] = ["001"]
    if fail == "migration-plan": body["planAvailable"] = False
elif route.startswith("/api/v1/admin/api-keys"):
    # The admin API-key lifecycle the lane now drives itself: the administrator mints this run's
    # scoped principal, that principal is refused the same surface, and teardown revokes it.
    presented = event["headers"].get("x-api-key")
    override = os.environ.get("HONUA_LAMBDA_CERT_DENIED_KEY", "")
    admin = presented is not None and presented == (os.environ.get("HONUA_LAMBDA_CERT_ADMIN_KEY") or "offline-sensitive-canary")
    minted = next((k for k in s["keys"] if k["value"] == presented), None)
    scoped = bool(override and presented == override) or bool(minted and minted["status"] == "active")
    tail = route[len("/api/v1/admin/api-keys"):].strip("/")
    method = event["requestContext"]["http"]["method"]
    def public(key):
        return {"id":key["id"], "name":key["name"], "keyPrefix":key["value"][:8],
                "permissions":key["permissions"], "status":key["status"]}
    if admin and method == "POST" and not tail:
        request = json.loads(event["body"])
        assert request["permissions"] == ["read:layers"], request
        assert request["name"].startswith("honua-cert-denied-"), request
        # An abandoned credential is a standing one, so the mint itself has to bound the key.
        assert request["expiresAt"].endswith("Z") and request["expiresAt"] > "2026", request
        if fail == "mint-refused":
            status, body = 400, {"success":False, "message":"Validation failed: permissions are required"}
        else:
            key = {"id":"00000000-0000-4000-8000-%012d" % (len(s["keys"]) + 1), "name":request["name"],
                   "value":"offline-minted-key-%d" % (len(s["keys"]) + 1),
                   "permissions":request["permissions"], "status":"active"}
            s["keys"].append(key)
            # A create can be applied and still lose its response; the record is already there.
            if fail == "mint-response-lost": bad()
            status, body = 201, {"success":True, "data":{"apiKey":public(key), "key":key["value"]}}
    elif admin and method == "POST" and tail.endswith("/revoke"):
        target = next((k for k in s["keys"] if k["id"] == tail.split("/")[0]), None)
        if target is None or fail == "revoke-refused":
            status, body = 404, {"success":False, "message":"API key not found"}
        else:
            target["status"] = "revoked"
            s["revoked"].append(target["id"])
            status, body = 200, {"success":True, "data":public(target)}
    elif admin and method == "GET" and tail.endswith("/effective-permissions"):
        target = next((k for k in s["keys"] if k["id"] == tail.split("/")[0]), None)
        if fail == "local-key-store" and function.endswith(":live"):
            target = None
        if target is None:
            status, body = 404, {"success":False, "message":"API key not found"}
        else:
            status = 200
            body = {"success":True, "data":{"id":target["id"], "name":target["name"], "status":target["status"],
                                            "permissions":target["permissions"],
                                            "canAuthenticate":target["status"] == "active"}}
    elif admin and method == "GET" and not tail:
        status, body = 200, {"success":True, "data":[public(k) for k in s["keys"]]}
    elif scoped:
        # Authenticated, not authorized: the documented empty 403 with zero records.
        status = 401 if fail in ("scoped-unauthenticated", "denied-key-missing") else 200 if fail in ("scoped-allowed", "denied-key-leaks") else 403
        body = {"data":[{"id":"leaked","name":"leaked"}]} if fail in ("scoped-records", "denied-key-leaks") else ""
        if fail == "denied-key-missing":
            body = {"type":"https://honua.io/problems/admin", "title":"Unauthorized", "status":401,
                    "detail":"API key required. Provide a valid API key in the X-API-Key header."}
            headers = {"www-authenticate":'ApiKey realm="Honua Admin", header="X-API-Key", Basic realm="Honua Admin"'}
    else:
        status = 200 if fail == "denial-status" else 401
        body = {"status":401, "type":"https://honua.io/problems/admin"}
        if fail == "denial-body": body["status"] = 403
        if fail == "denial-records": body["data"] = [{"key":"leaked"}]
        if fail == "denial-nested": body["unexpectedExtension"] = {"records":[{"key":"leaked"}]}
elif route == "/api/v1/admin/license/status":
    # The server's own verdict on the envelope it was handed. This is what separates a deployment
    # that was never given a license from one carrying an envelope the server refused - opposite
    # owners that the 402 alone cannot tell apart.
    edition, validation = ("Community", "NoLicenseConfigured") if fail == "unlicensed-edits" else \
                          ("Pro", "InvalidSignature") if fail == "license-rejected" else \
                          ("Pro", "Expired") if fail == "license-blocked" else ("Pro", "Valid")
    entitlements = [] if fail == "unlicensed-edits" else [
        {"key":"editing.featureserver-edits", "name":"FeatureServer Editing", "isActive":validation == "Valid"}]
    body = {"success":True, "data":{"edition":edition, "isValid":validation == "Valid",
                                    "validationState":validation, "entitlements":entitlements}}
elif route.endswith("/0/query"):
    if "returnCountOnly" in event["rawQueryString"]: body = {"count":9 if fail == "query" else 10}
    else: body = {"features":[{"attributes":{"name":n}} for n in ["alpha","beta","gamma","delta","epsilon","zeta","eta","theta","iota","lambda"]]}
    if fail == "fixture-names": body = {"features":[]}
elif route.endswith("/10/query"):
    body = {"features": [{"attributes":{"objectid":1234,"name":"wrong" if fail == "readback" else "honua-certrun-123-1"}}] if s["row"] else []}
elif route.endswith("/addFeatures"):
    if fail in ("unlicensed-edits", "license-rejected"):
        body = geoservices_entitlement_refusal(fail)
    else:
        s["row"] = True
        body = {"addResults":[{"success": fail != "create", "objectId":1234}]}
elif route.endswith("/deleteFeatures"):
    if fail in ("unlicensed-edits", "license-rejected"):
        # FeatureServerEditsHandler enforces the entitlement once for the whole GeoServices write
        # surface, so the lane's own cleanup delete is refused exactly like the add. Model that:
        # a stub that refused only addFeatures would let the teardown "succeed" against a
        # deployment where nothing can be written at all.
        body = geoservices_entitlement_refusal(fail)
    else:
        form = parse_qs(event["body"])
        if fail != "delete-remains" or "where" in form: s["row"] = False
        body = {"deleteResults":[{"success":fail != "delete" or "where" in form,"objectId":1234}]}
else: bad()
log = "REPORT RequestId: offline-id Duration: 20.00 ms Billed Duration: 30 ms Init Duration: 150.25 ms"
if event["requestContext"]["http"]["userAgent"] == "honua-lambda-preview-cert":
    # Only the cold-start evidence invoke carries that user agent. Lambda proactively initializes an
    # execution environment while a create or a configuration update settles; STUB_PROACTIVE_INIT is
    # how many of this run's environments it wins that race for, and an invoke landing on one
    # reports no Init Duration and emits no INIT_REPORT at all.
    s["stream"] = "2026/09/08/[$LATEST]offline-environment-%d" % s["environments"]
    s["warm"] = s["environments"] <= int(os.environ.get("STUB_PROACTIVE_INIT", "0"))
    s["invokes"].append("warm" if s["warm"] else "cold")
    # Only a stream whose environment initialized inside its invoke ever carries an INIT_REPORT.
    if not s["warm"]: s["cold_streams"].append(s["stream"])
    if s["warm"]: log = "REPORT RequestId: offline-id Duration: 20.00 ms Billed Duration: 30 ms"
if fail == "report": log = "no report"
if fail == "cold-start": log = "REPORT RequestId: offline-id Duration: 20.00 ms"
if fail == "cold-zero": log = "REPORT RequestId: offline-id Init Duration: 0 ms"
if fail == "init-error": log = "INIT_REPORT Init Duration: 21364.18 ms\tPhase: invoke\tStatus: error\nREPORT RequestId: offline-id Duration: 20.00 ms"
if os.environ.get("STUB_INIT_PHASE") == "invoke": log = "INIT_REPORT Init Duration: 21364.18 ms\tPhase: invoke\tStatus: ok\nREPORT RequestId: offline-id Duration: 20.00 ms Billed Duration: 30 ms"
response = {"statusCode":status,"body":body if isinstance(body,str) else json.dumps(body)}
if headers: response["headers"] = headers
# A function error replaces the HTTP response with the runtime's own error document, and the tail
# carries the platform's account of the initialization that produced it. "standing-invoke" fails
# only the alias, which is live run 25 exactly: the candidate served and minted, and the first
# invoke of the standing alias came back as a Lambda function error.
# "standing-invoke" fails the FIRST qualified invoke, before the shift: live run 25 exactly.
# "candidate-phase-invoke" fails the alias AFTER the shift, when the alias serves the candidate's
# own published version and a failure on it belongs to the candidate, not to the cert stack.
function_error = (fail == "invoke" or (fail == "standing-invoke" and ":" in function and not s["shifted"])
                  or (fail == "candidate-phase-invoke" and phase == "candidate"))
if function_error:
    # The application writes to the same stream as the platform, and the lane holds none of what it
    # can print: HONUA_ADMIN_PASSWORD arrives as a reference, so the resolved password is a value no
    # redaction set can contain. Only the platform's own lines may be echoed.
    application = "resolved administrator offline-resolved-password-never-echoed for this environment"
    if fail == "candidate-phase-invoke":
        # An invocation that ran out of time: the environment initialized, so there is no failed
        # INIT_REPORT to read the phase from, and the runtime names no error type.
        log = application + "\nREPORT RequestId: offline-id Duration: 60000.00 ms Billed Duration: 60000 ms"
        response = {"errorMessage": "2026-09-09T04:00:00Z offline-id Task timed out after 60.00 seconds"}
    else:
        log = ("INIT_REPORT Init Duration: 7412.55 ms\tPhase: init\tStatus: error\tError Type: Runtime.ExitError\n"
               + application + "\nSTART RequestId: offline-id Version: " + version)
        message = "Error: Runtime exited with error: exit status 134"
        # A server-authored diagnostic can quote a credential; the lane must never echo one. Only
        # the candidate's variant carries one, so the other still proves the message is reported.
        if fail == "invoke": message += " while reading " + os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"]
        response = {"errorType": "Runtime.ExitError", "errorMessage": message}
response_path.write_text(json.dumps(response))
meta = {"StatusCode":200,"ExecutedVersion":"99" if fail == "executed-version" else version,"LogResult":base64.b64encode(log.encode()).decode()}
if function_error: meta["FunctionError"] = "Unhandled"
emit(meta)
'''


# The exact GHCR manifest bytes for the Lambda AOT artifact the certification lane mirrors
# (ghcr.io/honua-io/honua-server@sha256:0b526ccb...). Recorded verbatim so its own sha256 is the
# source digest below; the manifest-level check re-derives that rather than trusting it.
SOURCE_MANIFEST_DIGEST = "sha256:0b526ccb871b9a5cbd82312d5736a5bfccd1a21112e628e5c2ca5d26726f744c"
SOURCE_MANIFEST_BYTES = "{\n  \"schemaVersion\": 2,\n  \"mediaType\": \"application/vnd.oci.image.manifest.v1+json\",\n  \"config\": {\n    \"mediaType\": \"application/vnd.oci.image.config.v1+json\",\n    \"digest\": \"sha256:a2b0f20d60115dab0c3495bf389e13e6d775ad2af7a033bcf48eda261647a5db\",\n    \"size\": 6059\n  },\n  \"layers\": [\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:966c395d29cb24a3faf7e04f32878fe5778819d4132daee4f47e2aaf7b9af924\",\n      \"size\": 29751109\n    },\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:0f33241ccad066b49bf271998eab59d2e90585fdfc5f0815b9a6122b84b77fc3\",\n      \"size\": 19182933\n    },\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:6f51f4d8c488469bbc7d297812604fc03b24e30647eb98e7620de299965dbf93\",\n      \"size\": 3565\n    },\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:2d530074b2f41088f249387531c9ed5c28772703e0318eaec1b0c666a9392bc5\",\n      \"size\": 1695995\n    },\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:e8fc15bb84621ebcc19ea28d0afff42af969dedbb7a6a2f2212b8acbecfe5965\",\n      \"size\": 111\n    },\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:fce22e166fa89f78d58a6e3525dd3a9661766784c25b79939b76812473bdad4e\",\n      \"size\": 166833620\n    },\n    {\n      \"mediaType\": \"application/vnd.oci.image.layer.v1.tar+gzip\",\n      \"digest\": \"sha256:9a26356f16529501ce89bc1e52be9d86278cc7404729c48fa65aa92313af8dc1\",\n      \"size\": 2684831\n    }\n  ]\n}"

# ECR stores the same blobs under a Docker schema 2 envelope, so the manifest digest changes while
# the config blob and every layer blob stay byte-identical.
SCHEMA2_MEDIA_TYPES = {
    "application/vnd.oci.image.manifest.v1+json": "application/vnd.docker.distribution.manifest.v2+json",
    "application/vnd.oci.image.config.v1+json": "application/vnd.docker.container.image.v1+json",
    "application/vnd.oci.image.layer.v1.tar+gzip": "application/vnd.docker.image.rootfs.diff.tar.gzip",
}


def as_ecr_schema2(manifest):
    converted = json.loads(json.dumps(manifest))
    for node in [converted, converted["config"], *converted["layers"]]:
        node["mediaType"] = SCHEMA2_MEDIA_TYPES[node["mediaType"]]
    return converted


# Derived by the lane from the pinned revision and source digest the offline run supplies.
CANDIDATE_TAG = "candidate-" + "a" * 12 + "-" + "a" * 12 + "-x86_64"


class AdminCredentialTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("lambda_certification", SCRIPT_PATH.with_name("lambda-certification.py"))
        self.driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.driver)
        self.environment = unittest.mock.patch.dict(os.environ, {
            "HONUA_LAMBDA_CERT_ADMIN_KEY": "", "HONUA_LAMBDA_CERT_DENIED_KEY": "denied-key",
            "HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE": "true",
            "REALAWS_CERT_LAMBDA_FUNCTION": "honua-cert-cert-server", "AWS_REGION": "us-east-1",
            "GITHUB_ACTIONS": "false"}, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.current = {"Configuration": {
            "FunctionArn": "arn:aws:lambda:us-east-1:123456789012:function:honua-cert-cert-server",
            "Environment": {"Variables": {"HONUA_ADMIN_PASSWORD": "aws:secretsmanager:cert/admin"}}}}

    def test_reference_names_arns_and_version_options(self):
        arn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:cert/admin-Ab12Cd"
        cases = [
            ("aws:secretsmanager:cert/admin", ["--secret-id", "cert/admin"]),
            ("AWS:SecretsManager:" + arn, ["--secret-id", arn, "--region", "us-west-2"]),
            ("aws:secretsmanager:cert/admin?versionStage=AWSPREVIOUS&versionId=id%2B1",
             ["--secret-id", "cert/admin", "--version-stage", "AWSPREVIOUS", "--version-id", "id+1"]),
            ("aws:secretsmanager:cert/admin?VersionStage=first&versionStage=last+stage",
             ["--secret-id", "cert/admin", "--version-stage", "last+stage"])]
        for reference, expected in cases:
            with self.subTest(reference=reference):
                self.assertEqual(expected, self.driver.secret_reference(reference)[1])

    def test_invalid_references_never_echo_the_value(self):
        for reference in (None, "", "inline-private-key", "aws:secretsmanager:",
                          "aws:secretsmanager:bad\n::warning::private", "aws:secretsmanager:name?versionId=%ZZ",
                          "aws:secretsmanager:arn:aws:ssm:us-east-1:123456789012:parameter/admin"):
            with self.subTest(reference=reference):
                with self.assertRaises(RuntimeError) as error:
                    self.driver.secret_reference(reference)
                if reference:
                    self.assertNotIn(reference, str(error.exception))

    def test_resolution_uses_standing_configuration_and_caches_in_memory(self):
        with unittest.mock.patch.object(self.driver, "config", return_value=self.current) as config, \
                unittest.mock.patch.object(self.driver, "aws", return_value={"SecretString": "resolved-private-key"}) as aws:
            self.assertEqual("resolved-private-key", self.driver.admin_key())
            self.assertEqual("resolved-private-key", self.driver.admin_key())
            config.assert_called_once_with("honua-cert-cert-server")
            aws.assert_called_once_with("secretsmanager", "get-secret-value", "--secret-id", "cert/admin")
            self.assertEqual("[redacted]", self.driver.redacted("resolved-private-key", 60))
            self.assertEqual("", os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"])

    def test_override_takes_precedence_without_configuration_or_secret_reads(self):
        os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"] = "override-private-key"
        with unittest.mock.patch.object(self.driver, "aws") as aws, unittest.mock.patch.object(self.driver, "config") as config:
            self.assertEqual("override-private-key", self.driver.admin_key())
            aws.assert_not_called()
            config.assert_not_called()

    def test_masks_resolved_and_override_values_with_workflow_escaping(self):
        value = "private%value\r\n::warning::injected"
        for override in (False, True):
            self.driver._admin_key = None
            os.environ["GITHUB_ACTIONS"] = "true"
            os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"] = value if override else ""
            output = io.StringIO()
            with unittest.mock.patch.object(self.driver, "aws", return_value={"SecretString": value}), \
                    unittest.mock.patch("sys.stdout", output):
                self.assertEqual(value, self.driver.admin_key(self.current))
                self.driver.admin_key()
            self.assertEqual("::add-mask::private%25value%0D%0A::warning::injected\n", output.getvalue())
        output = io.StringIO()
        os.environ["GITHUB_ACTIONS"] = "false"
        with unittest.mock.patch("sys.stdout", output):
            self.driver.mask_secret(value)
        self.assertEqual("", output.getvalue())

    def test_binary_empty_missing_and_denied_principal(self):
        cases = [({"SecretBinary": "YmluYXJ5LWtleQ=="}, "binary-key"),
                 ({"SecretString": ""}, None), ({"SecretString": "  "}, None), ({}, None),
                 ({"SecretString": "denied-key"}, None)]
        for response, expected in cases:
            self.driver._admin_key = None
            with self.subTest(response=response), unittest.mock.patch.object(self.driver, "aws", return_value=response):
                if expected:
                    self.assertEqual(expected, self.driver.admin_key(self.current))
                else:
                    with self.assertRaises(RuntimeError):
                        self.driver.admin_key(self.current)

    def test_access_denied_reports_only_the_required_secret_arn(self):
        arn = "arn:aws:secretsmanager:us-west-2:123456789012:secret:cert/admin-Ab12Cd"
        for identifier, pattern in (("cert/admin", "arn:aws:secretsmanager:us-east-1:123456789012:secret:cert/admin-??????"), (arn, arn)):
            self.current["Configuration"]["Environment"]["Variables"]["HONUA_ADMIN_PASSWORD"] = "aws:secretsmanager:" + identifier
            with unittest.mock.patch.object(self.driver, "aws", side_effect=self.driver.SecretReadDenied("private diagnostic")):
                with self.assertRaises(RuntimeError) as error:
                    self.driver.admin_key(self.current)
                self.assertIn("STOP: OIDC role needs secretsmanager:GetSecretValue on " + pattern, str(error.exception))
                self.assertNotIn("private diagnostic", str(error.exception))


class LambdaPreviewLaneContractTests(unittest.TestCase):
    def run_lane(self, failure="", ecr=None, **overrides):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            stub = directory / "stub"
            stub.write_text(STUB)
            stub.chmod(0o755)
            for executable in ("aws", "crane", "docker", "dotnet", "sleep"):
                (directory / executable).symlink_to(stub)
            state_path = directory / "state.json"
            original = "123456789012.dkr.ecr.us-east-1.amazonaws.com/standing@sha256:" + "a" * 64
            state_path.write_text(json.dumps({"calls": [], "backend": [], "function": False, "logs": False,
                                             "alias": "7", "image": original, "versions": ["7"], "deleted_versions": [],
                                             "shifted": False, "rolledback": False, "row": False,
                                             "ecr": ecr, "mirrored": None, "vpc": None,
                                             "environments": 0, "nonces": [], "invokes": [],
                                             "warm": False, "stream": None, "platform_queries": 0,
                                             "stream_scoped": False, "stream_queries": [], "cold_streams": [],
                                             "deleted_tags": [], "keys": [], "revoked": []}))
            env = {**os.environ, "PATH": str(directory) + ":" + os.environ["PATH"], "STUB_STATE": str(state_path),
                   "STUB_FAIL": failure, "STUB_INDEX": "", "HONUA_LAMBDA_SOURCE_IMAGE": "ghcr.io/honua-io/honua-server:nightly-lambda-aot-test-amd64",
                   "HONUA_LAMBDA_SOURCE_DIGEST": "sha256:" + "a" * 64, "HONUA_LAMBDA_SERVER_REVISION": "a" * 40,
                   "HONUA_LAMBDA_CERT_DENIED_KEY": "offline-scoped-key", "HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE": "false",
                   "HONUA_LAMBDA_ARCHITECTURE": "x86_64", "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1",
                   "AWS_REGION": "us-east-1", "REALAWS_CERT_LAMBDA_FUNCTION": "honua-cert-cert-server",
                   "REALAWS_CERT_LAMBDA_ALIAS": "live", "HONUA_LAMBDA_CERT_ADMIN_KEY": "", "GITHUB_ACTIONS": "false",
                   "HONUA_LAMBDA_WRITE_BASE_URL": "https://cert.lambda-url.us-east-1.on.aws",
                   "HONUA_DEMO_BASE_URL": "https://demo.invalid", "HONUA_LAMBDA_PREVIEW_RECEIPT": str(directory / "receipt.json"),
                   "HONUA_LAMBDA_PREVIEW_REPOSITORY": "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-cert-cert-lambda-preview",
                   "HONUA_LAMBDA_PREVIEW_EXECUTION_ROLE_ARN": "arn:aws:iam::123456789012:role/cert", **overrides}
            # A stale success must be invalidated even when required inputs are absent.
            (directory / "receipt.json").write_text('{"result":"pass"}')
            result = subprocess.run(["bash", str(SCRIPT_PATH)], env=env, capture_output=True, text=True, timeout=180)
            receipt_path = directory / "receipt.json"
            receipt = json.loads(receipt_path.read_text()) if receipt_path.exists() else {}
            self.assertNotIn("offline-sensitive-canary", result.stdout + result.stderr + json.dumps(receipt))
            self.assertNotIn("offline-scoped-key", result.stdout + result.stderr + json.dumps(receipt))
            state = json.loads(state_path.read_text())
            # The key the lane mints for itself is a credential too: it never reaches the log or the receipt.
            for key in state["keys"]:
                self.assertNotIn(key["value"], result.stdout + result.stderr + json.dumps(receipt))
            return result, receipt, state, original

    def test_pass_both_manifest_architectures(self):
        for architecture in ("x86_64", "arm64"):
            with self.subTest(architecture=architecture):
                result, receipt, state, original = self.run_lane(HONUA_LAMBDA_ARCHITECTURE=architecture)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual("pass", receipt["result"])
                self.assertEqual(architecture, receipt["deployment"]["architecture"])
                self.assertEqual(150.25, receipt["verification"]["coldStartInitDurationMs"])
                self.assertEqual("init", receipt["verification"]["coldStartInitPhase"])
                self.assertEqual("tail", receipt["verification"]["coldStartEvidenceSource"])
                self.assertTrue(receipt["verification"]["coldStartEnvironmentForced"])
                self.assertEqual(1, receipt["verification"]["coldStartInvokeAttempts"])
                self.assertEqual(["cold"], state["invokes"])
                serving = receipt["serving"]
                self.assertEqual({"beforeVersion":"7", "afterVersion":"8", "rollbackVersion":"7"}, serving["alias"])
                for phase in ("deployed", "baseline", "candidate", "rollback"):
                    self.assertEqual(10, serving[phase]["fixture"]["actualRows"])
                    self.assertEqual(0, serving[phase]["write"]["remainingRows"])
                    self.assertEqual(403, serving[phase]["authorization"]["actualStatus"])
                    self.assertEqual(401, serving[phase]["authorization"]["anonymousStatus"])
                self.assertEqual(["shift", "rollback"], state["backend"])
                self.assertEqual(["8"], state["deleted_versions"])
                self.assertEqual(original, state["image"])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_optional_override_skips_secret_reads(self):
        result, receipt, state, _ = self.run_lane("secret-access-denied", HONUA_LAMBDA_CERT_ADMIN_KEY="offline-sensitive-canary")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        self.assertFalse(any("secretsmanager" in call for call in state["calls"]))
        self.assertIn("HONUA_LAMBDA_CERT_ADMIN_KEY: ${{ secrets.REALAWS_CERT_ADMIN_KEY }}", WORKFLOW)

    def test_secret_failures_stop_before_resources_or_invocations(self):
        for failure in ("secret-access-denied", "secret-read-failed", "secret-empty"):
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("noProof", receipt["serving"]["result"])
                self.assertFalse(state["function"] or state["logs"] or state["backend"])
                self.assertFalse(any("invoke" in call or "docker" in call for call in state["calls"]))
                if failure == "secret-access-denied":
                    self.assertIn("secretsmanager:GetSecretValue on arn:aws:secretsmanager:us-east-1:123456789012:secret:offline-admin-??????", result.stderr)

    def test_mirror_copies_the_exact_source_manifest_and_never_re_encodes_it(self):
        source = "sha256:" + "a" * 64
        result, receipt, state, _ = self.run_lane()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("ghcr.io/honua-io/honua-server@" + source, state["mirrored"])
        self.assertEqual(source, receipt["artifact"]["sourceDigest"])
        self.assertEqual(source, receipt["artifact"]["sourcePlatformDigest"])
        self.assertEqual("sha256:" + "c" * 64, receipt["artifact"]["sourceConfigDigest"])
        self.assertEqual("sha256:" + "b" * 64, receipt["artifact"]["ecrDigest"])
        self.assertTrue(receipt["artifact"]["configDigestPreserved"])
        self.assertTrue(receipt["artifact"]["rootfsPreserved"])
        self.assertEqual("crane", receipt["artifact"]["mirrorTool"])
        # A docker pull/tag/push round trip re-serialises the config and breaks byte-exactness.
        self.assertIn("crane copy", SCRIPT)
        self.assertNotIn("docker push", SCRIPT)
        self.assertNotIn("docker tag", SCRIPT)
        self.assertFalse([call for call in state["calls"] if call[:2] in (["docker", "push"], ["docker", "tag"])])

    def test_multi_platform_source_mirrors_the_candidate_platform_child(self):
        result, receipt, state, _ = self.run_lane(STUB_INDEX="index")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        child = "sha256:" + "9" * 64
        self.assertEqual("ghcr.io/honua-io/honua-server@" + child, state["mirrored"])
        self.assertEqual(child, receipt["artifact"]["sourcePlatformDigest"])
        self.assertEqual("sha256:" + "a" * 64, receipt["artifact"]["sourceDigest"])

    def test_rerun_survives_the_immutable_candidate_tag(self):
        """The certification repository is tag-immutable, so a rerun must not depend on overwriting."""
        source = "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64
        exact = {"digest": "sha256:" + "b" * 64, "config": "sha256:" + "c" * 64,
                 "layers": ["sha256:" + "d" * 64], "rootfs": ["sha256:" + "1" * 64, "sha256:" + "2" * 64]}
        # What the live run 34064826386 hit: a tag left behind by the earlier re-encoding mirror.
        stale = {"digest": "sha256:" + "5" * 64, "config": "sha256:" + "f" * 64,
                 "layers": ["sha256:" + "e" * 64], "rootfs": ["sha256:" + "0" * 64]}
        cases = ((None, "pushed", source, []), (exact, "skipped-existing", None, []),
                 (stale, "replaced-stale", source, [CANDIDATE_TAG]))
        for seeded, outcome, mirrored, deleted in cases:
            with self.subTest(outcome=outcome):
                result, receipt, state, _ = self.run_lane(ecr=seeded)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual("pass", receipt["result"])
                self.assertEqual(outcome, receipt["artifact"]["mirrorOutcome"])
                self.assertEqual(mirrored, state["mirrored"])
                self.assertEqual(deleted, [d.split("=", 1)[1] for d in state["deleted_tags"]])
                self.assertEqual(bool(mirrored), any(call[:1] == ["crane"] for call in state["calls"]))
                # Whatever the outcome, the artifact ECR ends up holding is still verified in full.
                self.assertEqual(exact["digest"], receipt["artifact"]["ecrDigest"])
                self.assertEqual(exact["config"], receipt["artifact"]["sourceConfigDigest"])

    def test_immutable_tag_handling_fails_closed(self):
        stale = {"digest": "sha256:" + "5" * 64, "config": "sha256:" + "f" * 64,
                 "layers": ["sha256:" + "e" * 64], "rootfs": []}
        for failure, seeded in (("describe-error", None), ("stale-delete", stale), ("manifest-error", stale)):
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure, ecr=seeded)
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertEqual("noProof", receipt["serving"]["result"])
                # An unreadable repository is never mistaken for an absent tag, and a stale mirror
                # that could not be removed is never left for the verification below to accept.
                self.assertIsNone(state["mirrored"])
                self.assertFalse(any(call[:1] == ["crane"] for call in state["calls"]))
                self.assertFalse(state["function"] or state["logs"] or state["row"])
        # A manifest lookup that failed is not evidence of a stale artifact: nothing is deleted, and
        # the seeded artifact the earlier certification handed off is still there.
        _, _, state, _ = self.run_lane("manifest-error", ecr=stale)
        self.assertEqual([], state["deleted_tags"])
        self.assertEqual(stale, state["ecr"])

    def test_candidate_tag_separates_the_two_supported_architectures(self):
        """One multi-platform pin certified for both architectures must not share a mirror tag."""
        tags = {}
        for architecture in ("x86_64", "arm64"):
            with self.subTest(architecture=architecture):
                result, _, state, _ = self.run_lane(STUB_INDEX="index", HONUA_LAMBDA_ARCHITECTURE=architecture)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                addressed = {argument.split("=", 1)[1] for call in state["calls"] for argument in call
                             if argument.startswith("imageTag=")}
                copied = {call[3].rsplit(":", 1)[-1] for call in state["calls"] if call[:2] == ["crane", "copy"]}
                self.assertEqual(1, len(addressed | copied), addressed | copied)
                tags[architecture] = (addressed | copied).pop()
                self.assertTrue(tags[architecture].endswith("-" + architecture), tags[architecture])
        self.assertNotEqual(tags["x86_64"], tags["arm64"])

    def test_source_index_without_exactly_one_candidate_child_fails_closed(self):
        for mode in ("no-match", "ambiguous"):
            with self.subTest(mode=mode):
                result, receipt, state, _ = self.run_lane(STUB_INDEX=mode)
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertIsNone(state["mirrored"])
                self.assertIsNone(state["ecr"])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_each_check_fails_closed(self):
        for failure in ("architecture", "ecr-platform", "revision", "adapter", "digest", "mirror", "layers", "rootfs",
                        "skip-config", "missing-db", "missing-admin-key", "blank-admin-key",
                        "admin-401", "admin-unconfigured", "admin-unresolvable",
                        "resolved-image", "health-status", "health-body", "invoke",
                        "report", "cold-start", "cold-zero", "init-error", "cloudwatch", "migrations", "migration-pending", "migration-plan",
                        "query", "fixture-names", "create", "readback", "delete", "delete-remains",
                        "denial-status", "denial-body", "denial-records", "denial-nested", "scoped-unauthenticated", "scoped-allowed", "scoped-records", "executed-version", "weighted",
                        "denied-key-missing", "denied-key-leaks", "mint-refused", "mint-response-lost", "revoke-refused",
                        "standing-invoke", "candidate-phase-invoke",
                        "unlicensed-edits", "license-rejected", "license-blocked",
                        "function-delete", "log-delete", "version-delete", "ownership", "get-function-transient"):
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode, failure)
                self.assertNotEqual("pass", receipt.get("result"), failure)
                self.assertEqual("noProof", receipt["serving"]["result"])
                if failure not in ("function-delete", "ownership"):
                    self.assertFalse(state["function"], failure)
                if failure not in ("log-delete", "ownership"):
                    self.assertFalse(state["logs"], failure)
                self.assertFalse(state["row"], failure)

    def test_cold_start_missing_from_the_tail_is_read_from_cloudwatch(self):
        """The 4 KB invoke tail can cut the INIT_REPORT off; CloudWatch carries the same line."""
        result, receipt, state, _ = self.run_lane("cold-start", STUB_CLOUDWATCH_INIT="invoke")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        self.assertEqual(21364.18, receipt["verification"]["coldStartInitDurationMs"])
        self.assertEqual("invoke", receipt["verification"]["coldStartInitPhase"])
        self.assertEqual("cloudwatch", receipt["verification"]["coldStartEvidenceSource"])

    def test_cold_start_beyond_the_init_window_is_recorded_from_init_report(self):
        """Init longer than Lambda's init window is re-run in the first invoke; its INIT_REPORT is the evidence."""
        result, receipt, state, _ = self.run_lane(STUB_INIT_PHASE="invoke")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        self.assertEqual(21364.18, receipt["verification"]["coldStartInitDurationMs"])
        self.assertEqual("invoke", receipt["verification"]["coldStartInitPhase"])

    def test_a_pre_initialized_environment_is_retried_until_the_invoke_is_cold(self):
        """Lambda pre-initializes the environment it activates; the certified invoke needs a new one."""
        result, receipt, state, _ = self.run_lane(STUB_PROACTIVE_INIT="1")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        self.assertEqual(150.25, receipt["verification"]["coldStartInitDurationMs"])
        self.assertTrue(receipt["verification"]["coldStartEnvironmentForced"])
        self.assertEqual(2, receipt["verification"]["coldStartInvokeAttempts"])
        # Every attempt reconfigured the function, so every invoke ran on an environment this run
        # forced into existence, and the pass came from the one that was actually cold.
        self.assertEqual(2, len(state["nonces"]))
        self.assertEqual(len(set(state["nonces"])), len(state["nonces"]))
        self.assertEqual(["warm", "cold"], state["invokes"])

    def test_an_environment_that_is_never_cold_fails_closed(self):
        """The retry forces fresh environments; it never lets a warm invoke stand in for a cold one."""
        result, receipt, state, _ = self.run_lane(STUB_PROACTIVE_INIT="9")
        self.assertNotEqual(0, result.returncode)
        self.assertNotEqual("pass", receipt.get("result"))
        self.assertEqual("noProof", receipt["serving"]["result"])
        self.assertIn("no forced execution environment produced a positive cold-start", result.stderr)
        self.assertEqual(3, len(state["nonces"]))
        self.assertEqual(["warm", "warm", "warm"], state["invokes"])
        self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_a_warm_attempts_late_line_is_never_credited_to_the_certified_invoke(self):
        """Each attempt reads its own environment's stream, so the group's other attempts cannot supply the evidence."""
        result, receipt, state, _ = self.run_lane("cold-start", STUB_PROACTIVE_INIT="1",
                                                  STUB_CLOUDWATCH_INIT="invoke")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        self.assertEqual(2, receipt["verification"]["coldStartInvokeAttempts"])
        self.assertEqual("cloudwatch", receipt["verification"]["coldStartEvidenceSource"])
        self.assertEqual(["warm", "cold"], state["invokes"])
        # The warm attempt searched only its own stream and found nothing there; the pass came from
        # the second environment's stream, which is the one the recorded request id ran in.
        self.assertTrue(state["stream_queries"])
        self.assertEqual([state["stream"]], state["cold_streams"])
        self.assertNotEqual(state["stream_queries"][0], state["stream"])
        self.assertEqual(state["stream"], state["stream_queries"][-1])

    def test_cold_start_evidence_survives_cloudwatch_delivery_lag(self):
        """Platform-line delivery lags by minutes; the bounded poll waits, scoped to the invoke's stream."""
        result, receipt, state, _ = self.run_lane("cold-start", STUB_CLOUDWATCH_INIT="invoke",
                                                  STUB_CLOUDWATCH_LAG="6")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        self.assertEqual("cloudwatch", receipt["verification"]["coldStartEvidenceSource"])
        self.assertEqual(21364.18, receipt["verification"]["coldStartInitDurationMs"])
        self.assertEqual(1, receipt["verification"]["coldStartInvokeAttempts"])
        # The query narrowed to the execution environment the certified invoke ran in, and kept
        # asking past the empty answers rather than reading the lag as an absent cold start.
        self.assertTrue(state["stream_scoped"])
        self.assertGreater(state["platform_queries"], 6)

    def test_indeterminate_get_function_is_never_recorded_as_deletion(self):
        """A throttle or service error during teardown must not publish teardown.functionDeleted."""
        result, receipt, state, _ = self.run_lane("get-function-transient")
        self.assertNotEqual(0, result.returncode)
        self.assertNotEqual("pass", receipt.get("result"))
        self.assertNotIn("teardown", receipt)
        self.assertEqual("noProof", receipt["serving"]["result"])
        self.assertIn("get-function was indeterminate", result.stderr)
        self.assertIn("TooManyRequestsException", result.stderr)
        # Only the not-found answer completes the poll, so the retries were spent, not short-circuited.
        polls = [call for call in state["calls"]
                 if call[:2] == ["aws", "lambda"] and "get-function" in call
                 and any(argument.startswith("honua-certrun-") for argument in call)]
        self.assertGreater(len(polls), 30)

    def test_create_receives_the_standing_functions_vpc_configuration(self):
        """The ephemeral function reaches the cert PostGIS over the standing private networking."""
        result, _, state, _ = self.run_lane()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual({"SubnetIds": ["subnet-cert"], "SecurityGroupIds": ["sg-cert"]}, state["vpc"])

    def test_missing_standing_vpc_configuration_fails_closed_before_create(self):
        result, receipt, state, _ = self.run_lane("missing-vpc")
        self.assertNotEqual(0, result.returncode)
        self.assertNotEqual("pass", receipt.get("result"))
        self.assertEqual("noProof", receipt["serving"]["result"])
        self.assertIsNone(state["vpc"])
        self.assertFalse(state["function"] or state["logs"] or state["row"])
        # Preparation refuses first; the deploy step re-asserts the same fact at the point of use,
        # so an empty paramfile can never reach the CLI as an opaque argument error.
        self.assertIn('if [[ ! -s "$scratch/$paramfile" ]]; then', SCRIPT)
        self.assertIn("""((.SubnetIds // []) | length) > 0 and ((.SecurityGroupIds // []) | length) > 0""", SCRIPT)

    def test_a_standing_environment_without_the_admin_credential_fails_closed_before_create(self):
        """The lane clones authentication and never injects a credential of its own, so a standing
        environment that lost the variable can only answer every administrative assertion with 401."""
        # Whitespace is not a credential either: ResolveAdminPasswordAsync treats an all-whitespace
        # value as unconfigured, so an environment carrying one must fail at the same point.
        for failure in ("missing-admin-key", "blank-admin-key"):
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertEqual("noProof", receipt["serving"]["result"])
                self.assertIn("Standing function carries no HONUA_ADMIN_PASSWORD", result.stderr)
                # Refused from the standing configuration: nothing was mirrored, created or invoked.
                self.assertIsNone(state["mirrored"])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_unauthorized_serving_assertion_names_the_credential_and_the_challenge(self):
        """Run 21 (34222614774) stopped at a 401 whose title was "Unauthorized" and nothing else, and
        separating a deployment with no administrator from one this key no longer matches took the
        standing configuration and a manual probe. One run must now answer that question."""
        # The three causes the operator table in lambda-certification.md separates: no administrator
        # at all, a reference the handler could not resolve, and one this key no longer matches.
        cases = (("admin-401", "present", "secretsmanager-reference", "API key required"),
                 ("admin-unresolvable", "present", "secretsmanager-reference", "Admin authentication not configured"),
                 ("admin-unconfigured", "absent", "none", "Admin authentication not configured"))
        for failure, presence, source, detail in cases:
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("noProof", receipt["serving"]["result"])
                assertion = [line for line in result.stderr.splitlines() if line.startswith("serving-assertion:")]
                diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-401:")]
                self.assertEqual(1, len(diagnosis), result.stderr)
                self.assertIn("phase=deployed", assertion[0])
                self.assertIn("status=401", assertion[0])
                self.assertIn("variable=HONUA_ADMIN_PASSWORD", diagnosis[0])
                self.assertIn("presence=" + presence, diagnosis[0])
                self.assertIn("source=" + source, diagnosis[0])
                # The scheme that refused, parsed out of the folded challenge header rather than
                # split on its commas: "header=" and "charset=" are parameters, not schemes.
                self.assertIn("challenge=ApiKey+Basic", diagnosis[0])
                self.assertIn(detail, diagnosis[0])
                # Names and the server's own fixed strings only, never the value behind the variable.
                self.assertNotIn("offline-admin", diagnosis[0])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_an_in_body_geoservices_error_names_the_cause_and_the_owner(self):
        """Run 28 (34320738962) reached the run-owned write with every earlier assertion green and
        stopped on `serving-assertion: path=.../10/addFeatures status=200 expected=200
        body-kind=json error=402`. GeoServices reports a refused operation as HTTP 200 with the
        reason only in the envelope, so neither the status nor the code said whether the fixture,
        the payload or the deployment was wrong, and 402 has two opposite owners: a function that
        was never given a license, and one carrying an envelope the server refused. One run must
        now answer both."""
        cases = (("unlicensed-edits", "absent", "none", "0", "Community", "NoLicenseConfigured",
                  "install a license that includes"),
                 ("license-rejected", "present", "secretsmanager-reference", "1", "Pro", "InvalidSignature",
                  "A valid paid license is required"))
        for failure, presence, source, keys, edition, validation, detail in cases:
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("noProof", receipt["serving"]["result"])
                assertion = [line for line in result.stderr.splitlines()
                             if line.startswith("serving-assertion:")]
                # The entitlement is enforced once for the whole GeoServices write surface, so the
                # run-owned add is refused and the lane's own cleanup delete is refused after it.
                # Both are reported; neither is swallowed by the teardown.
                self.assertEqual(2, len(assertion), result.stderr)
                self.assertIn("path=/rest/services/test_service/FeatureServer/10/addFeatures", assertion[0])
                self.assertIn("path=/rest/services/test_service/FeatureServer/10/deleteFeatures", assertion[1])
                for line in assertion:
                    # The status the lane expected and got: the refusal is entirely in the body.
                    self.assertIn("status=200 expected=200", line)
                    self.assertIn("error=402", line)
                    # The two fields the failing run never carried.
                    self.assertIn("message=Payment Required", line)
                    self.assertIn(detail, line)
                    # The server's own details array, joined with a separator the redaction keeps.
                    self.assertIn("entitlement: editing.featureserver-edits", line)
                diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-402:")]
                self.assertEqual(2, len(diagnosis), result.stderr)
                for line in diagnosis:
                    self.assertIn("phase=deployed", line)
                    # Both refused operations are GeoServices edits, so both name the entitlement
                    # rather than reporting a whole-deployment license block.
                    self.assertIn("entitlement=editing.featureserver-edits", line)
                    # Which side owns it: the function's own configuration, by variable NAME only...
                    self.assertIn("variable=Licensing__LicenseContentSecretRef", line)
                    self.assertIn("presence=" + presence, line)
                    self.assertIn("source=" + source, line)
                    self.assertIn("trusted-keys=" + keys, line)
                    # ...and the server's own verdict on what it made of it.
                    self.assertIn("edition=" + edition, line)
                    self.assertIn("validation=" + validation, line)
                    self.assertIn("entitled=false", line)
                    # Never the envelope, the licensee or the key behind the variable.
                    self.assertNotIn("offline-license", line)
                    self.assertNotIn("offline-trusted-public-key", line)
                # A refused write commits nothing, so there was no row for the teardown to lose.
                self.assertFalse(state["row"])

    def test_a_deployment_wide_license_block_is_never_read_as_an_edit_entitlement(self):
        """402 has two owners on this lane. `LicenseOperationMiddleware` refuses every data route of
        a deployment whose license expired or went unusable, and that arrives on the FIRST serving
        assertion as a problem document, not a GeoServices envelope. Naming the FeatureServer edit
        entitlement there would send a whole-deployment block to the wrong owner, so the refused
        path decides: only the GeoServices write operations claim the entitlement."""
        result, receipt, state, _ = self.run_lane("license-blocked")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        assertion = [line for line in result.stderr.splitlines() if line.startswith("serving-assertion:")]
        self.assertEqual(1, len(assertion), result.stderr)
        # The lane never reaches the scratch layer: the first administrative read is already refused.
        self.assertIn("path=/api/v1/admin/observability/migrations", assertion[0])
        self.assertIn("status=402", assertion[0])
        diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-402:")]
        self.assertEqual(1, len(diagnosis), result.stderr)
        self.assertIn("entitlement=none", diagnosis[0])
        self.assertNotIn("editing.featureserver-edits", diagnosis[0])
        # The license state is still reported: the envelope is there, and the server refused it.
        self.assertIn("presence=present", diagnosis[0])
        self.assertIn("edition=Pro", diagnosis[0])
        self.assertIn("validation=Expired", diagnosis[0])
        self.assertFalse(state["row"])

    def test_a_lambda_function_error_says_which_side_failed_and_how(self):
        """Run 25 (34305710517) stopped on a bare "Lambda invocation failed": the receipt showed the
        candidate had minted and revoked this run's key, so the invocation that failed was the first
        one of the standing alias — and nothing in the job log said so, nor whether the function had
        died initializing or thrown while serving. One run must now answer both."""
        result, receipt, state, _ = self.run_lane("standing-invoke")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        # The receipt run 25 produced: the candidate served well enough to mint and to revoke, and
        # the standing alias never confirmed it could see the record.
        denied = receipt["serving"]["deniedKey"]
        self.assertTrue(denied["created"] and denied["revoked"])
        self.assertFalse(denied["sharedStoreVerified"])
        diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-invoke:")]
        self.assertEqual(1, len(diagnosis), result.stderr)
        self.assertIn("phase=denied-key-shared-store", diagnosis[0])
        # Which side of the certification failed, without naming either function.
        self.assertIn("target=standing-alias", diagnosis[0])
        self.assertIn("function-error=Unhandled", diagnosis[0])
        self.assertIn("error-type=Runtime.ExitError", diagnosis[0])
        # An initialization failure, on the platform's own INIT_REPORT verdict: Runtime.ExitError
        # alone would not have established the phase.
        self.assertIn("kind=init", diagnosis[0])
        self.assertIn("Runtime exited with error: exit status 134", diagnosis[0])
        # The platform's own account of the same invocation, from the invoke's own tail.
        tail = [line for line in result.stderr.splitlines() if line.startswith("serving-invoke-log:")]
        self.assertTrue(any("INIT_REPORT" in line and "Status: error" in line for line in tail), tail)
        self.assertTrue(any(line.startswith("serving-invoke-log: START ") for line in tail), tail)
        # The application's own stdout shares that stream and is never echoed: the lane cannot
        # redact a resolved secret it never held.
        self.assertNotIn("offline-resolved-password-never-echoed", result.stdout + result.stderr)
        self.assertFalse(state["function"] or state["logs"] or state["row"])
        # The operator table that says which owner a target= points at.
        documentation = (ROOT / "scripts/cloud/lambda-certification.md").read_text()
        self.assertIn("serving-invoke:", documentation)
        self.assertIn("target=standing-alias", documentation)

    def test_a_candidate_phase_alias_failure_is_attributed_to_the_candidate(self):
        """serve() shifts the standing alias to the newly published candidate version, so from the
        candidate phase on, a qualified invocation is the candidate. Attributing it to the cert
        stack would send the artifact's own defect to the wrong owner."""
        result, receipt, state, _ = self.run_lane("candidate-phase-invoke")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-invoke:")]
        self.assertEqual(1, len(diagnosis), result.stderr)
        self.assertIn("phase=candidate", diagnosis[0])
        # Qualified, and still the candidate: the version Lambda executed is the one just published.
        self.assertIn("target=candidate", diagnosis[0])
        self.assertIn("executed-version=8", diagnosis[0])
        # An invocation that ran out of time, not an initialization failure and not a handler throw.
        self.assertIn("kind=timeout", diagnosis[0])
        self.assertIn("error-type=none", diagnosis[0])
        self.assertIn("Task timed out after 60.00 seconds", diagnosis[0])
        # The alias is put back and the candidate version removed even though the run failed on it.
        self.assertEqual("7", state["alias"])
        self.assertFalse(state["function"] or state["logs"] or state["row"])

    def certification_driver(self):
        spec = importlib.util.spec_from_file_location(
            "lambda_certification", ROOT / "scripts/cloud/lambda-certification.py")
        driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(driver)
        return driver

    def test_a_runtime_exit_alone_never_claims_the_phase(self):
        """Lambda raises Runtime.ExitError whenever the runtime process dies, during an invocation
        as readily as during initialization. Without a failed INIT_REPORT the phase is unestablished,
        and saying "init" would send a serving crash to startup."""
        module = self.certification_driver()
        self.assertEqual("init", module.invoke_failure_kind(
            {"errorType": "Runtime.ExitError"},
            "INIT_REPORT Init Duration: 1.0 ms\tPhase: init\tStatus: error"))
        self.assertEqual("runtime-exit", module.invoke_failure_kind({"errorType": "Runtime.ExitError"}, ""))
        self.assertEqual("runtime-exit", module.invoke_failure_kind(
            {"errorType": "Runtime.ExitError"},
            "INIT_REPORT Init Duration: 1.0 ms\tPhase: init\tStatus: ok"))
        self.assertEqual("init", module.invoke_failure_kind({"errorType": "Init.Failure"}, ""))
        self.assertEqual("timeout", module.invoke_failure_kind({"errorMessage": "Task timed out after 3.00 seconds"}, ""))
        self.assertEqual("handler", module.invoke_failure_kind({"errorType": "System.InvalidOperationException"}, ""))
        self.assertEqual("unknown", module.invoke_failure_kind({}, ""))

    def test_cloned_environment_values_join_the_redaction_set(self):
        """The lane clones the standing environment onto the candidate, so a value it never chose can
        come back inside a server-authored message. Declared references stay out: they are pointers
        the lane already reports by kind, and redacting them would drop every line naming the store."""
        module = self.certification_driver()
        with tempfile.TemporaryDirectory() as temp, \
                unittest.mock.patch.dict(module.os.environ,
                                         {"HONUA_LAMBDA_CERT_ADMIN_KEY": "offline-admin-key"}, clear=True):
            directory = Path(temp)
            (directory / "environment.json").write_text(json.dumps({"Variables": {
                "ConnectionStrings__DefaultConnection": "Host=cert;Password=inline-cloned-secret-value",
                "HONUA_ADMIN_PASSWORD": "aws:secretsmanager:arn:aws:secretsmanager:us-east-1:1:secret:a",
                "HONUA_SKIP_MIGRATIONS": "false"}}))
            # Nothing is cloned until the lane has a prepared environment to read.
            self.assertIn("inline-cloned-secret-value", module.redacted("was inline-cloned-secret-value", 200))
            module.load_cloned_secrets(directory)
            self.assertEqual("[redacted]", module.redacted("failed on Password=inline-cloned-secret-value", 200))
            # A reference is not a credential, and a configuration flag is not one either.
            self.assertIn("secretsmanager", module.redacted("resolving aws:secretsmanager:arn ref", 200))
            self.assertIn("false", module.redacted("HONUA_SKIP_MIGRATIONS is false", 200))
            # A diagnostic must never fail on its own account: an unreadable paramfile is not fatal.
            module.load_cloned_secrets(directory / "absent")

    def test_a_failed_cold_start_invoke_reports_through_the_same_classifier(self):
        """The shell stage invokes the candidate itself; its failures must read the same way rather
        than through a second, drifting copy of the classifier and the redaction in bash."""
        result, receipt, state, _ = self.run_lane("invoke")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-invoke:")]
        self.assertEqual(1, len(diagnosis), result.stderr)
        self.assertIn("target=candidate", diagnosis[0])
        self.assertIn("path=/healthz/live", diagnosis[0])
        self.assertIn("kind=init", diagnosis[0])
        self.assertIn("error-type=Runtime.ExitError", diagnosis[0])
        # The credential the stub's error message quoted never reaches the log: the message is
        # dropped whole rather than filtered down to a fragment of the key. run_lane asserts the
        # canary is absent everywhere; this pins which field absorbed it.
        self.assertIn("error-message=[redacted]", diagnosis[0])
        self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_diagnostic_redaction_compares_before_it_filters_or_truncates(self):
        """A key is matched against the original text, not the normalized-and-capped one, and a long
        fragment of one counts as the key: filtering and truncating are what produce those."""
        spec = importlib.util.spec_from_file_location(
            "lambda_certification", ROOT / "scripts/cloud/lambda-certification.py")
        driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(driver)
        # A key carrying a character the sanitizer strips, and one longer than the cap: filtering or
        # truncating first would leave "abcdefKey" and a surviving 60-character prefix in the log.
        admin, denied = "abc$def-Key!", "s" * 200
        environment = {**os.environ, "HONUA_LAMBDA_CERT_ADMIN_KEY": admin,
                       "HONUA_LAMBDA_CERT_DENIED_KEY": denied}
        with unittest.mock.patch.dict(driver.os.environ, environment, clear=True):
            for value in (admin, denied, "leading " + admin + " trailing", denied[:120]):
                self.assertEqual("[redacted]", driver.redacted(value, 60), value[:40])
            # A server-authored refusal detail still comes through intact.
            detail = "Admin authentication not configured"
            self.assertEqual(detail, driver.redacted(detail, 120))
            driver._denied["value"] = "offline-minted-sensitive"
            self.assertEqual("[redacted]", driver.redacted(driver._denied["value"], 60))
            self.assertEqual("[redacted]", driver.challenge_schemes(
                {"WWW-Authenticate": driver._denied["value"] + ' realm="private"'}))
            self.assertEqual("ApiKey+Basic", driver.challenge_schemes(
                {"WWW-Authenticate": 'ApiKey realm="private", Basic realm="private"'}))
            self.assertEqual("json", driver.body_kind([{"id": "record"}]))

    def test_denial_requires_an_empty_http_body_not_a_json_empty_string(self):
        spec = importlib.util.spec_from_file_location(
            "lambda_certification", ROOT / "scripts/cloud/lambda-certification.py")
        driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(driver)
        environment = {"HONUA_LAMBDA_WRITE_BASE_URL": "https://cert.example.test"}
        for raw in ("", '""', " "):
            with self.subTest(raw=raw):
                def respond(*args):
                    Path(args[-1]).write_text(json.dumps({"statusCode": 403, "body": raw}))
                    return {"StatusCode": 200}
                with unittest.mock.patch.dict(driver.os.environ, environment), \
                        unittest.mock.patch.object(driver, "aws", side_effect=respond):
                    status, body, _, _ = driver.invoke("candidate", driver.ADMIN_API_KEYS,
                                                      authenticated=False)
                self.assertEqual(403, status)
                self.assertEqual(raw, body)
                self.assertEqual(raw == "", body == "")

    def test_create_failure_reports_a_redacted_aws_error(self):
        result, receipt, state, _ = self.run_lane("create-error")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        summary = "\n".join(line for line in result.stderr.splitlines()
                            if line.startswith("create-function error:"))
        # The error code and operation are what make the next failure diagnosable from the job log.
        self.assertIn("InvalidParameterValueException", summary)
        self.assertIn("CreateFunction", summary)
        # The inputs the message quoted back are not: neither the environment value nor the account.
        self.assertNotIn("aws:secretsmanager:offline-db", summary)
        self.assertIn("[redacted]", summary)
        self.assertNotIn("123456789012", summary)
        self.assertIn("[account]", summary)
        self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_failure_after_shift_rolls_back_and_cleans_candidate(self):
        for failure in ("candidate-query", "backend-shift", "rollback-query", "backend-rollback"):
            with self.subTest(failure=failure):
                result, receipt, state, original = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("noProof", receipt["serving"]["result"])
                self.assertEqual(["shift", "rollback"], state["backend"])
                self.assertEqual("7", state["alias"])
                self.assertEqual(["8"], state["deleted_versions"])
                self.assertEqual(original, state["image"])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_failed_rollback_never_deletes_a_version_still_serving(self):
        result, receipt, state, original = self.run_lane("backend-rollback-unapplied")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        self.assertEqual("8", state["alias"])
        self.assertEqual([], state["deleted_versions"])
        self.assertEqual("8", receipt["serving"]["candidateVersion"])
        self.assertIsNone(receipt["serving"]["alias"]["rollbackVersion"])
        self.assertFalse(receipt["serving"]["teardown"]["candidateVersionDeleted"])
        self.assertEqual(original, state["image"])
        self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_lost_publish_response_deletes_only_owned_new_version(self):
        result, receipt, state, original = self.run_lane("publish-response-lost")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        self.assertEqual([], state["backend"])
        self.assertEqual(["8"], state["deleted_versions"])
        self.assertEqual(["7"], state["versions"])
        self.assertEqual(original, state["image"])

    def test_write_url_guardrail_and_target_binding(self):
        for url in ("https://demo.invalid", "https://DEMO.invalid/", "https://demo.invalid:443", "https://unrelated.invalid"):
            with self.subTest(url=url):
                result, receipt, state, _ = self.run_lane(HONUA_LAMBDA_WRITE_BASE_URL=url)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("noProof", receipt["serving"]["result"])
                self.assertFalse(state["function"] or state["backend"])
                self.assertFalse(any("invoke" in call for call in state["calls"]))

    def test_missing_required_inputs_fail(self):
        for name in ("HONUA_LAMBDA_ARCHITECTURE", "REALAWS_CERT_LAMBDA_FUNCTION", "REALAWS_CERT_LAMBDA_ALIAS",
                     "HONUA_DEMO_BASE_URL"):
            with self.subTest(name=name):
                result, receipt, state, _ = self.run_lane(**{name: ""})
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertEqual([], state["calls"])

    def test_denied_principal_is_minted_per_run_and_revoked_at_teardown(self):
        """The denial assertion carries its own scoped key instead of a hand-minted bootstrap secret.

        Run 23 (34243173689) could not distinguish a lost bootstrap key from an authorization leak.
        """
        result, receipt, state, _ = self.run_lane()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        denied = receipt["serving"]["deniedKey"]
        self.assertEqual("minted", denied["source"])
        self.assertEqual(["read:layers"], denied["permissions"])
        self.assertEqual("honua-cert-denied-123-1", denied["name"])
        self.assertTrue(denied["created"])
        self.assertTrue(denied["sharedStoreVerified"])
        # The deletion is recorded, and it is the server's own view of the record that says so.
        self.assertTrue(denied["revoked"])
        self.assertIs(False, denied["canAuthenticate"])
        self.assertEqual(0, denied["activeAfterTeardown"])
        for phase in ("deployed", "baseline", "candidate", "rollback"):
            self.assertEqual(403, receipt["serving"][phase]["authorization"]["actualStatus"])
            self.assertEqual("minted", receipt["serving"][phase]["authorization"]["principalSource"])
        # Exactly one key was minted, it was this run's, and it is revoked in the cert database.
        self.assertEqual(1, len(state["keys"]))
        self.assertEqual("honua-cert-denied-123-1", state["keys"][0]["name"])
        self.assertEqual(["read:layers"], state["keys"][0]["permissions"])
        self.assertEqual("revoked", state["keys"][0]["status"])
        self.assertEqual([state["keys"][0]["id"]], state["revoked"])

    def test_bootstrap_denied_key_override_is_still_accepted(self):
        """The cert secret keeps working for one release, and an override mints nothing."""
        result, receipt, state, _ = self.run_lane(HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE="true")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("pass", receipt["result"])
        denied = receipt["serving"]["deniedKey"]
        self.assertEqual("override", denied["source"])
        self.assertFalse(denied["created"])
        self.assertFalse(denied["revoked"])
        self.assertEqual([], state["keys"])
        self.assertEqual("override", receipt["serving"]["deployed"]["authorization"]["principalSource"])

    def test_a_denied_principal_that_is_not_forbidden_says_which_way_it_failed(self):
        """Run 23 could not say whether the scoped key was gone or the server had leaked records."""
        cases = ((["denied-key-missing"], 401, "no", "json", "minted", "honua-cert-denied-123-1", "active"),
                 (["denied-key-leaks"], 200, "yes", "json", "minted", "honua-cert-denied-123-1", "active"),
                 # Run 23 itself: a bootstrap override the lane can say nothing else about.
                 (["denied-key-missing", "override"], 401, "no", "json", "override",
                  "HONUA_LAMBDA_CERT_DENIED_KEY", "unknown"))
        for selector, status, authenticated, kind, principal, key, record in cases:
            with self.subTest(case=selector):
                overrides = {"HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE": "true"} if "override" in selector else {}
                result, receipt, state, _ = self.run_lane(selector[0], **overrides)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("noProof", receipt["serving"]["result"])
                diagnosis = [line for line in result.stderr.splitlines() if line.startswith("serving-403:")]
                self.assertTrue(diagnosis, result.stderr)
                self.assertIn("phase=deployed", diagnosis[0])
                self.assertIn("status=%d" % status, diagnosis[0])
                self.assertIn("body-kind=" + kind, diagnosis[0])
                self.assertIn("authenticated=" + authenticated, diagnosis[0])
                self.assertIn("principal=" + principal, diagnosis[0])
                self.assertIn("key=" + key, diagnosis[0])
                self.assertIn("record=" + record, diagnosis[0])
                if status == 401:
                    # The challenge names the scheme that refused, exactly as the 401 line does.
                    self.assertIn("challenge=ApiKey+Basic", diagnosis[0])
                else:
                    # A leak (honua-server#4386) is counted, not quoted.
                    self.assertIn("records=1", diagnosis[0])
                    self.assertNotIn("leaked", diagnosis[0])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_a_lost_mint_response_still_revokes_the_record_it_created(self):
        """A create can be applied and lose its response; the row it left behind is this run's."""
        result, receipt, state, _ = self.run_lane("mint-response-lost")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("noProof", receipt["serving"]["result"])
        self.assertEqual(1, len(state["keys"]))
        # Resolved by this run's unique key name, since the lane never saw the id.
        self.assertEqual("revoked", state["keys"][0]["status"])
        self.assertTrue(receipt["serving"]["deniedKey"]["revoked"])
        self.assertFalse(receipt["serving"]["deniedKey"]["created"])
        self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_a_denial_key_left_behind_fails_the_run(self):
        """Serving can pass and the run still not certify: the credential must not outlive it."""
        result, receipt, state, _ = self.run_lane("revoke-refused")
        self.assertNotEqual(0, result.returncode)
        self.assertNotEqual("pass", receipt.get("result"))
        self.assertEqual("noProof", receipt["serving"]["result"])
        # The serving assertions themselves passed; teardown is what refused the run.
        self.assertEqual(403, receipt["serving"]["deployed"]["authorization"]["actualStatus"])
        self.assertFalse(receipt["serving"]["deniedKey"]["revoked"])
        self.assertIn("Denial key", result.stderr)
        self.assertEqual("active", state["keys"][0]["status"])

    def test_the_denied_key_secret_is_no_longer_a_required_bootstrap_input(self):
        documentation = (ROOT / "scripts/cloud/lambda-certification.md").read_text()
        self.assertNotIn("HONUA_LAMBDA_CERT_ADMIN_KEY HONUA_LAMBDA_CERT_DENIED_KEY", WORKFLOW)
        self.assertNotIn("  HONUA_LAMBDA_CERT_DENIED_KEY\n", SCRIPT)
        # Still passed through, so an existing bootstrap keeps working for one release.
        self.assertIn("HONUA_LAMBDA_CERT_DENIED_KEY: ${{ secrets.REALAWS_CERT_DENIED_KEY }}", WORKFLOW)
        self.assertIn("HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE: ${{ inputs.use_denied_key_override }}", WORKFLOW)
        self.assertRegex(WORKFLOW, r"use_denied_key_override:\n(?:.*\n)*?        default: false")
        self.assertIn("**Optional override, deprecated.**", documentation)
        self.assertIn("mints its own scoped `read:layers` principal per run", documentation)
        self.assertIn("serving-403:", documentation)

    def test_mint_requires_shared_redis_and_cross_target_key_visibility(self):
        for failure in ("missing-redis", "alias-missing-redis", "local-key-store"):
            with self.subTest(failure=failure):
                result, receipt, state, _ = self.run_lane(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertIn("shared Redis", result.stderr)
                self.assertFalse(state["function"] or state["logs"] or state["row"])
                if failure == "local-key-store":
                    self.assertFalse(receipt["serving"]["deniedKey"]["sharedStoreVerified"])
                    self.assertTrue(receipt["serving"]["deniedKey"]["revoked"])
                else:
                    self.assertEqual([], state["keys"])

    def test_explicit_override_requires_a_key_and_missing_default_key_mints(self):
        result, receipt, state, _ = self.run_lane(HONUA_LAMBDA_CERT_DENIED_KEY="")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("minted", receipt["serving"]["deniedKey"]["source"])
        result, receipt, state, _ = self.run_lane(HONUA_LAMBDA_CERT_DENIED_KEY="",
                                                 HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE="true")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("override was requested", result.stderr)
        self.assertEqual([], state["keys"])

    def test_manifest_check_accepts_ecr_schema2_and_rejects_a_re_encoded_config(self):
        """Manifest-level check on the real artifact: config/rootfs identity, never manifest identity."""
        self.assertEqual(SOURCE_MANIFEST_DIGEST,
                         "sha256:" + hashlib.sha256(SOURCE_MANIFEST_BYTES.encode()).hexdigest())
        source = json.loads(SOURCE_MANIFEST_BYTES)

        # The lane reads exactly these two facts off both manifests; keep the test bound to it.
        for manifest_variable in ("source_manifest", "ecr_manifest"):
            self.assertIn("jq -er '.config.digest' <<<\"$%s\"" % manifest_variable, SCRIPT)
            self.assertIn("jq -ce '[.layers[].digest]' <<<\"$%s\"" % manifest_variable, SCRIPT)

        def compared(manifest):
            return manifest["config"]["digest"], [layer["digest"] for layer in manifest["layers"]]

        mirrored = as_ecr_schema2(source)
        self.assertEqual(compared(source), compared(mirrored))
        # ...even though ECR's envelope is a different artifact by manifest digest, which is why the
        # lane must not compare manifest digests.
        self.assertNotEqual(SOURCE_MANIFEST_DIGEST,
                            "sha256:" + hashlib.sha256(json.dumps(mirrored).encode()).hexdigest())

        # A docker pull/tag/push round trip re-serialises the config blob into a new digest: the
        # live run's exact failure mode, which must stay a failure.
        re_encoded = as_ecr_schema2(source)
        re_encoded["config"] = {**re_encoded["config"], "digest": "sha256:" + "f" * 64}
        self.assertNotEqual(compared(source), compared(re_encoded))
        rewritten_layer = as_ecr_schema2(source)
        rewritten_layer["layers"][0] = {**rewritten_layer["layers"][0], "digest": "sha256:" + "e" * 64}
        self.assertNotEqual(compared(source), compared(rewritten_layer))

    def test_workflow_uses_cert_oidc_and_shared_substrate_lock(self):
        for text in ("environment: cert", "id-token: write", "vars.REALAWS_CERT_ROLE_ARN", "group: real-aws-certification",
                     "cancel-in-progress: false", "test-certify-lambda-preview.py", "LambdaDeployDriver.csproj",
                     "inputs.architecture", "ubuntu-24.04-arm", ".artifact.ecrDigest",
                     "CRANE_VERSION: v0.22.1", "sha256sum --check --status"):
            self.assertIn(text, WORKFLOW)
        self.assertNotIn("AWS_ACCESS_KEY_ID", WORKFLOW + SCRIPT)
        self.assertNotIn("AWS_SECRET_ACCESS_KEY", WORKFLOW + SCRIPT)

    def test_backend_is_production_source_and_not_raw_cli_alias_update(self):
        project = (ROOT / "scripts/cloud/lambda-deploy-driver/LambdaDeployDriver.csproj").read_text()
        driver = (ROOT / "scripts/cloud/lambda-deploy-driver/Program.cs").read_text()
        helper = (ROOT / "scripts/cloud/lambda-certification.py").read_text()
        self.assertIn("AwsLambdaGitOpsDeployBackend.cs", project)
        self.assertIn("AwsLambdaAliasClient.cs", project)
        for call in ("backend.PlanAsync", "backend.StartAsync", "backend.RollbackAsync", "backend.ObserveAsync", "new AwsSdkLambdaAliasClient"):
            self.assertIn(call, driver)
        self.assertNotIn("update-alias", SCRIPT + helper)
        self.assertNotIn("delete-repository", SCRIPT + helper)

    def test_standing_limits_are_preserved_verbatim(self):
        limits = "plan summaries to the evidence thread BEFORE apply, STOP on any destroy beyond the lane's own teardown-of-what-it-created, no IAM trust widening, fingerprints only."
        self.assertIn(limits, SCRIPT)
        self.assertIn(limits, WORKFLOW)


if __name__ == "__main__":
    unittest.main(verbosity=2)
