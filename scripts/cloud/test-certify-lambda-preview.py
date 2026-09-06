"""Offline execution of the complete lane with stateful AWS CLI/container/backend doubles."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import tempfile
import unittest

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
def bad():
    path.write_text(json.dumps(s))
    sys.exit(1)
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
    # crane uploads the manifest and its blobs verbatim, so ECR keeps the exact config blob and rootfs.
    s["mirrored"] = args[1]
    s["ecr"] = {"config": manifest["config"]["digest"],
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
function = arg("--function-name", "")
if service == "sts": emit("123456789012")
if service == "ecr":
    if op == "get-login-password": emit("offline-password")
    if op == "describe-images":
        if not s["ecr"]: emit("None")
        emit("bad" if fail == "digest" else digest)
    if op == "batch-get-image":
        if not s["ecr"]: bad()
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
    if op == "filter-log-events": emit("0" if fail == "cloudwatch" else "1")
    bad()
if service != "lambda": bad()
if op == "get-function-url-config": emit({"FunctionUrl": "https://cert.lambda-url.us-east-1.on.aws/"})
if op == "wait": emit(None)
if op == "get-function":
    ephemeral = function.startswith("honua-certrun-")
    if ephemeral and not s["function"]: bad()
    image = repo + "@" + digest if ephemeral else s["image"]
    if arg("--qualifier") == "8": image = repo + "@" + digest
    if ephemeral and fail == "resolved-image": image = "wrong"
    query = arg("--query")
    if query == "Code.ResolvedImageUri": emit(image)
    if query == "Configuration.FunctionArn": emit("arn:offline:ephemeral")
    variables = {"ConnectionStrings__DefaultConnection": "aws:secretsmanager:offline-db",
                 "HONUA_ADMIN_PASSWORD": "aws:secretsmanager:offline-admin", "HONUA_SKIP_MIGRATIONS": "false"}
    if fail == "skip-config": variables["HONUA_SKIP_MIGRATIONS"] = "true"
    if fail == "missing-db": variables.pop("ConnectionStrings__DefaultConnection")
    emit({"Configuration": {"RevisionId": "rev", "Description": "honua-cert-run=123-1" if arg("--qualifier") == "8" else "standing", "PackageType": "Image", "Architectures": [os.environ["HONUA_LAMBDA_ARCHITECTURE"]],
         "Environment": {"Variables": variables}, "VpcConfig": {"SubnetIds":["subnet-cert"], "SecurityGroupIds":["sg-cert"]}},
         "Code": {"ResolvedImageUri": image}})
if op == "create-function":
    env = json.loads(Path(arg("--environment")[7:]).read_text())
    assert env["Variables"]["HONUA_SKIP_MIGRATIONS"] == "false"
    assert arg("--vpc-config").startswith("file://")
    assert arg("--architectures") == os.environ["HONUA_LAMBDA_ARCHITECTURE"]
    s["function"] = True
    emit({"FunctionArn":"arn:offline:ephemeral"})
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
if op != "invoke": bad()
payload = arg("--payload")
event = json.loads(Path(payload[7:]).read_text() if payload.startswith("file://") else payload)
# The shell's invoke has no --output json; Python's helper adds it after the response path.
response_path = Path(args[args.index("--payload")+2])
route = event["rawPath"]
status = 200
body = {}
version = s["alias"] if ":" in function else "$LATEST"
phase = "rollback" if s["rolledback"] else "candidate" if s["shifted"] else "deployed" if function.startswith("honua-certrun-") else "baseline"
if fail == "candidate-query" and phase == "candidate": fail = "query"
if fail == "rollback-query" and phase == "rollback": fail = "query"
if route == "/healthz/live":
    body = "Unwell" if fail == "health-body" else "Healthy"
    if fail == "health-status": status = 500
elif route.endswith("/migrations"):
    body = {"status":"succeeded", "isReady":True, "isFailed":False, "planAvailable":True, "upgradeRequired":False, "pendingScripts":[]}
    if fail == "migrations": body["status"] = "skipped"
    if fail == "migration-pending": body["pendingScripts"] = ["001"]
    if fail == "migration-plan": body["planAvailable"] = False
elif route == "/api/v1/admin/api-keys":
    status = 200 if fail == "denial-status" else 401
    body = {"status":401, "type":"https://honua.io/problems/admin"}
    if fail == "denial-body": body["status"] = 403
    if fail == "denial-records": body["data"] = [{"key":"leaked"}]
    if fail == "denial-nested": body["unexpectedExtension"] = {"records":[{"key":"leaked"}]}
    if "x-api-key" in event["headers"]:
        assert event["headers"]["x-api-key"] == "offline-scoped-key"
        status = 401 if fail == "scoped-unauthenticated" else 200 if fail == "scoped-allowed" else 403
        body = {"records":[1]} if fail == "scoped-records" else ""
elif route.endswith("/0/query"):
    if "returnCountOnly" in event["rawQueryString"]: body = {"count":9 if fail == "query" else 10}
    else: body = {"features":[{"attributes":{"name":n}} for n in ["alpha","beta","gamma","delta","epsilon","zeta","eta","theta","iota","lambda"]]}
    if fail == "fixture-names": body = {"features":[]}
elif route.endswith("/10/query"):
    body = {"features": [{"attributes":{"objectid":1234,"name":"wrong" if fail == "readback" else "honua-certrun-123-1"}}] if s["row"] else []}
elif route.endswith("/addFeatures"):
    s["row"] = True
    body = {"addResults":[{"success": fail != "create", "objectId":1234}]}
elif route.endswith("/deleteFeatures"):
    form = parse_qs(event["body"])
    if fail != "delete-remains" or "where" in form: s["row"] = False
    body = {"deleteResults":[{"success":fail != "delete" or "where" in form,"objectId":1234}]}
else: bad()
log = "REPORT RequestId: offline-id Duration: 20.00 ms Billed Duration: 30 ms Init Duration: 150.25 ms"
if fail == "report": log = "no report"
if fail == "cold-start": log = "REPORT RequestId: offline-id Duration: 20.00 ms"
if fail == "cold-zero": log = "REPORT RequestId: offline-id Init Duration: 0 ms"
response_path.write_text(json.dumps({"statusCode":status,"body":body if isinstance(body,str) else json.dumps(body)}))
meta = {"StatusCode":200,"ExecutedVersion":"99" if fail == "executed-version" else version,"LogResult":base64.b64encode(log.encode()).decode()}
if fail == "invoke": meta["FunctionError"] = "Unhandled"
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


class LambdaPreviewLaneContractTests(unittest.TestCase):
    def run_lane(self, failure="", **overrides):
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
                                             "ecr": None, "mirrored": None}))
            env = {**os.environ, "PATH": str(directory) + ":" + os.environ["PATH"], "STUB_STATE": str(state_path),
                   "STUB_FAIL": failure, "STUB_INDEX": "", "HONUA_LAMBDA_SOURCE_IMAGE": "ghcr.io/honua-io/honua-server:nightly-lambda-aot-test-amd64",
                   "HONUA_LAMBDA_SOURCE_DIGEST": "sha256:" + "a" * 64, "HONUA_LAMBDA_SERVER_REVISION": "a" * 40,
                   "HONUA_LAMBDA_CERT_DENIED_KEY": "offline-scoped-key", "HONUA_LAMBDA_ARCHITECTURE": "x86_64", "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1",
                   "AWS_REGION": "us-east-1", "REALAWS_CERT_LAMBDA_FUNCTION": "honua-cert-cert-server",
                   "REALAWS_CERT_LAMBDA_ALIAS": "live", "HONUA_LAMBDA_CERT_ADMIN_KEY": "offline-sensitive-canary",
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
            state = json.loads(state_path.read_text())
            return result, receipt, state, original

    def test_pass_both_manifest_architectures(self):
        for architecture in ("x86_64", "arm64"):
            with self.subTest(architecture=architecture):
                result, receipt, state, original = self.run_lane(HONUA_LAMBDA_ARCHITECTURE=architecture)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual("pass", receipt["result"])
                self.assertEqual(architecture, receipt["deployment"]["architecture"])
                self.assertEqual(150.25, receipt["verification"]["coldStartInitDurationMs"])
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
                        "skip-config", "missing-db", "resolved-image", "health-status", "health-body", "invoke",
                        "report", "cold-start", "cold-zero", "cloudwatch", "migrations", "migration-pending", "migration-plan",
                        "query", "fixture-names", "create", "readback", "delete", "delete-remains",
                        "denial-status", "denial-body", "denial-records", "denial-nested", "scoped-unauthenticated", "scoped-allowed", "scoped-records", "executed-version", "weighted",
                        "function-delete", "log-delete", "version-delete", "ownership"):
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
                     "HONUA_DEMO_BASE_URL", "HONUA_LAMBDA_CERT_ADMIN_KEY", "HONUA_LAMBDA_CERT_DENIED_KEY"):
            with self.subTest(name=name):
                result, receipt, state, _ = self.run_lane(**{name: ""})
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertEqual([], state["calls"])

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
