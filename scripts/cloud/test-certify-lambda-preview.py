"""Offline execution of the complete lane with stateful AWS CLI/container/backend doubles."""
from pathlib import Path
import json
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = ROOT / "scripts/cloud/certify-lambda-preview.sh"
SCRIPT = SCRIPT_PATH.read_text()
WORKFLOW = (ROOT / ".github/workflows/lambda-preview-certification.yml").read_text()

# One executable, symlinked as aws/docker/dotnet/sleep. Unknown calls fail rather than succeed.
STUB = r'''#!/usr/bin/env python3
import base64, json, os, sys
from pathlib import Path
from urllib.parse import parse_qs
args = sys.argv[1:]
name = Path(sys.argv[0]).name
path = Path(os.environ["STUB_STATE"])
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
arch = "arm64" if os.environ["HONUA_LAMBDA_ARCHITECTURE"] == "arm64" else "amd64"
if name == "sleep": emit(None)
if name == "docker":
    if args[:3] == ["buildx", "imagetools", "inspect"]: emit(manifest)
    if args[:2] == ["image", "inspect"]:
        if "Architecture" in args[-1]: emit("wrong" if fail == "architecture" or fail == "ecr-platform" and "ecr" in args[2] else arch)
        emit("e"*40 if fail == "revision" else os.environ["HONUA_LAMBDA_SERVER_REVISION"])
    if args[0] == "run" and fail == "adapter": bad()
    if args[0] in ("pull", "run", "tag", "push", "login"): emit(None)
    bad()
if name == "dotnet":
    action, previous, candidate = args[1], args[4], args[5]
    s["backend"].append(action)
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
    if op == "describe-images": emit("bad" if fail == "digest" else digest)
    if op == "batch-get-image":
        if fail == "mirror": manifest["config"]["digest"] = "sha256:"+"f"*64
        if fail == "layers": manifest["layers"] = []
        emit(manifest)
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
    emit({"Configuration": {"RevisionId": "rev", "PackageType": "Image", "Architectures": [os.environ["HONUA_LAMBDA_ARCHITECTURE"]],
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
if op == "publish-version": s["versions"].append("8"); emit({"Version":"8"})
if op == "list-aliases": emit({"Aliases":[{"FunctionVersion":s["alias"]}]})
if op == "list-versions-by-function": emit({"Versions":[{"Version":v} for v in s["versions"]]})
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
    assert "x-api-key" not in event["headers"]
    status = 200 if fail == "denial-status" else 401
    body = {"status":401, "type":"https://honua.io/problems/admin"}
    if fail == "denial-body": body["status"] = 403
    if fail == "denial-records": body["data"] = [{"key":"leaked"}]
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


class LambdaPreviewLaneContractTests(unittest.TestCase):
    def run_lane(self, failure="", **overrides):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            stub = directory / "stub"
            stub.write_text(STUB)
            stub.chmod(0o755)
            for executable in ("aws", "docker", "dotnet", "sleep"):
                (directory / executable).symlink_to(stub)
            state_path = directory / "state.json"
            original = "123456789012.dkr.ecr.us-east-1.amazonaws.com/standing@sha256:" + "a" * 64
            state_path.write_text(json.dumps({"calls": [], "backend": [], "function": False, "logs": False,
                                             "alias": "7", "image": original, "versions": ["7"], "deleted_versions": [],
                                             "shifted": False, "rolledback": False, "row": False}))
            env = {**os.environ, "PATH": str(directory) + ":" + os.environ["PATH"], "STUB_STATE": str(state_path),
                   "STUB_FAIL": failure, "HONUA_LAMBDA_SOURCE_IMAGE": "ghcr.io/honua-io/honua-server:nightly-lambda-aot-test-amd64",
                   "HONUA_LAMBDA_SOURCE_DIGEST": "sha256:" + "a" * 64, "HONUA_LAMBDA_SERVER_REVISION": "a" * 40,
                   "HONUA_LAMBDA_ARCHITECTURE": "x86_64", "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1",
                   "AWS_REGION": "us-east-1", "REALAWS_CERT_LAMBDA_FUNCTION": "honua-cert-cert-server",
                   "REALAWS_CERT_LAMBDA_ALIAS": "live", "HONUA_LAMBDA_CERT_ADMIN_KEY": "offline-sensitive-canary",
                   "HONUA_LAMBDA_WRITE_BASE_URL": "https://cert.lambda-url.us-east-1.on.aws",
                   "HONUA_DEMO_BASE_URL": "https://demo.invalid", "HONUA_LAMBDA_PREVIEW_RECEIPT": str(directory / "receipt.json"),
                   "HONUA_LAMBDA_PREVIEW_REPOSITORY": "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua-cert-cert-lambda-preview",
                   "HONUA_LAMBDA_PREVIEW_EXECUTION_ROLE_ARN": "arn:aws:iam::123456789012:role/cert", **overrides}
            result = subprocess.run(["bash", str(SCRIPT_PATH)], env=env, capture_output=True, text=True, timeout=45)
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
                    self.assertEqual(401, serving[phase]["authorization"]["actualStatus"])
                self.assertEqual(["shift", "rollback"], state["backend"])
                self.assertEqual(["8"], state["deleted_versions"])
                self.assertEqual(original, state["image"])
                self.assertFalse(state["function"] or state["logs"] or state["row"])

    def test_each_check_fails_closed(self):
        for failure in ("architecture", "ecr-platform", "revision", "adapter", "digest", "mirror", "layers",
                        "skip-config", "missing-db", "resolved-image", "health-status", "health-body", "invoke",
                        "report", "cold-start", "cold-zero", "cloudwatch", "migrations", "migration-pending", "migration-plan",
                        "query", "fixture-names", "create", "readback", "delete", "delete-remains",
                        "denial-status", "denial-body", "denial-records", "executed-version", "weighted",
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
                     "HONUA_DEMO_BASE_URL", "HONUA_LAMBDA_CERT_ADMIN_KEY"):
            with self.subTest(name=name):
                result, receipt, state, _ = self.run_lane(**{name: ""})
                self.assertNotEqual(0, result.returncode)
                self.assertNotEqual("pass", receipt.get("result"))
                self.assertEqual([], state["calls"])

    def test_workflow_uses_cert_oidc_and_shared_substrate_lock(self):
        for text in ("environment: cert", "id-token: write", "vars.REALAWS_CERT_ROLE_ARN", "group: real-aws-certification",
                     "cancel-in-progress: false", "test-certify-lambda-preview.py", "LambdaDeployDriver.csproj",
                     "inputs.architecture", ".artifact.ecrDigest"):
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
