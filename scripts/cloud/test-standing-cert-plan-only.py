#!/usr/bin/env python3
"""Offline private-runner boundary controls; no GitHub token or AWS calls."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import stat
import tempfile
import unittest
import unittest.mock as mock
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).with_name("standing-cert-plan-only.py")
spec = importlib.util.spec_from_file_location("standing_cert_plan_only", SCRIPT)
entry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(entry)
hosted_spec = importlib.util.spec_from_file_location("standing_cert_hosted", SCRIPT.with_name("standing-cert-hosted.py"))
hosted = importlib.util.module_from_spec(hosted_spec)
hosted_spec.loader.exec_module(hosted)


class StandingCertPlanWorkflow(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "iac"
        self.script = self.source / "infrastructure/terraform/validation/scripts/aws/standing_cert_plan.py"
        self.script.parent.mkdir(parents=True)
        self.script.write_text("# authored producer fixture\n")
        self.request = self.base / "request.json"
        self.token = self.base / "backend-token"
        self.revision = "a" * 40
        self.candidate = "b" * 64
        self.data = {"iacRevision": self.revision, "candidateDigest": self.candidate,
                     "backendTokenFile": str(self.token),
                     "sessionExpiresAtUtc": (datetime.now(timezone.utc) + timedelta(minutes=45)).isoformat()}
        self.save_request()
        self.env = {"GITHUB_REPOSITORY": "honua-io/honua-server", "GITHUB_REF": "refs/heads/trunk",
                    "ACTIONS_RUNTIME_TOKEN": "PRIVATE RUNTIME TOKEN",
                    "CERT_IAC_REVISION": self.revision, "CERT_CANDIDATE_SHA256": self.candidate,
                    "CERT_REQUEST_SHA256": hashlib.sha256(self.request.read_bytes()).hexdigest(),
                    "CERT_IAC_SOURCE": str(self.source), "CERT_REQUEST_FILE": str(self.request)}

    def save_request(self):
        self.request.write_text(json.dumps(self.data))
        self.request.chmod(0o600)

    def git(self, args, **kwargs):
        if args[-2:] == ["rev-parse", "HEAD"]:
            return (self.revision + "\n").encode()
        if "status" in args or "ls-files" in args:
            return b""
        return self.script.read_bytes()

    def test_private_packet_receipt_only_and_token_cleanup(self):
        receipt = {key: "fixture" for key in entry.PUBLIC_KEYS}
        receipt["schema"] = "honua.aws-cert-plan-public/v1"
        receipt["status"] = "planned-unapproved"
        receipt["approvalStatus"] = "not-approved"
        receipt["qualificationStatus"] = "not-evaluated"
        receipt["iacRevision"] = self.revision
        receipt["candidateDigest"] = self.candidate
        receipt["expiresAtUtc"] = (datetime.now(timezone.utc) + timedelta(minutes=20)).isoformat()
        for key in ("planSha256", "planMetadataDigest", "packetSha256"):
            receipt[key] = "c" * 64
        child = mock.Mock(returncode=0, stdout=json.dumps(receipt), stderr="PRIVATE SENTINEL")
        out = io.StringIO()
        with mock.patch.dict(os.environ, self.env, clear=True), \
             mock.patch.object(entry.subprocess, "check_output", side_effect=self.git), \
             mock.patch.object(entry.subprocess, "run", return_value=child) as run, \
             mock.patch.object(entry, "oidc_token", return_value="header.payload.signature"), \
             contextlib.redirect_stdout(out):
            entry.main()
        self.assertEqual(json.loads(out.getvalue()), receipt)
        self.assertNotIn("PRIVATE SENTINEL", out.getvalue())
        self.assertFalse(self.token.exists())
        self.assertEqual(run.call_args.args[0][-2:], ["--request", str(self.request)])
        self.assertNotIn("ACTIONS_RUNTIME_TOKEN", run.call_args.kwargs["env"])

    def test_changed_producer_refused_before_token(self):
        with mock.patch.dict(os.environ, self.env, clear=True), \
             mock.patch.object(entry.subprocess, "check_output", side_effect=[(self.revision + "\n").encode(), b"", b"", b"different"]), \
             mock.patch.object(entry, "oidc_token") as token:
            with self.assertRaisesRegex(ValueError, "producer source"):
                entry.main()
            token.assert_not_called()
        self.assertFalse(self.token.exists())

    def test_python_startup_override_refused_before_token(self):
        for name in ("PYTHONUSERBASE", "PYTHONWARNINGS", "PYTHONINSPECT"):
            with self.subTest(name=name), mock.patch.dict(os.environ,
                    dict(self.env, **{name: "unsafe"}), clear=True), \
                 mock.patch.object(entry, "oidc_token") as token:
                with self.assertRaisesRegex(ValueError, "environment override"):
                    entry.main()
                token.assert_not_called()

    def test_dirty_checkout_refused_before_token(self):
        with mock.patch.dict(os.environ, self.env, clear=True), \
             mock.patch.object(entry.subprocess, "check_output", side_effect=[(self.revision + "\n").encode(), b" M unreviewed.tf\n"]), \
             mock.patch.object(entry, "oidc_token") as token:
            with self.assertRaisesRegex(ValueError, "source drift"):
                entry.main()
            token.assert_not_called()

    def test_private_value_in_receipt_is_never_published(self):
        receipt = {key: "PRIVATE SENTINEL" for key in entry.PUBLIC_KEYS}
        receipt["expiresAtUtc"] = (datetime.now(timezone.utc) + timedelta(minutes=20)).isoformat()
        child = mock.Mock(returncode=0, stdout=json.dumps(receipt), stderr="PRIVATE SENTINEL")
        out = io.StringIO()
        with mock.patch.dict(os.environ, self.env, clear=True), \
             mock.patch.object(entry.subprocess, "check_output", side_effect=self.git), \
             mock.patch.object(entry.subprocess, "run", return_value=child), \
             mock.patch.object(entry, "oidc_token", return_value="header.payload.signature"), \
             contextlib.redirect_stdout(out):
            with self.assertRaisesRegex(ValueError, "plan refused"):
                entry.main()
        self.assertEqual(out.getvalue(), "")
        self.assertFalse(self.token.exists())

    def test_request_and_token_path_refusals(self):
        with mock.patch.dict(os.environ, self.env, clear=True):
            os.environ["CERT_REQUEST_SHA256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "request digest"):
                entry.main()
        link = self.base / "token-link"
        link.symlink_to(self.request)
        with self.assertRaisesRegex(ValueError, "token destination occupied"):
            entry.private_path(link, exists=False)
        self.assertEqual(stat.S_IMODE(self.request.stat().st_mode), 0o600)

    def test_sharded_github_token_host_and_host_substitution(self):
        valid = "https://pipelinesghubeus13.actions.githubusercontent.com/abc/_apis/token?api-version=2.0"
        with mock.patch.dict(os.environ, {"ACTIONS_ID_TOKEN_REQUEST_URL": valid,
                                          "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "PRIVATE"}, clear=True), \
             mock.patch.object(entry.urllib.request, "build_opener") as opener:
            opener.return_value.open.return_value = io.BytesIO(b'{"value":"header.payload.signature"}')
            self.assertEqual(entry.oidc_token(), "header.payload.signature")
            request = opener.return_value.open.call_args.args[0]
            self.assertEqual(request.get_header("Authorization"), "Bearer PRIVATE")
            self.assertIn("audience=sts.amazonaws.com", request.full_url)
        for host in ("pipelines.actions.githubusercontent.com.evil.invalid",
                     "actions.githubusercontent.com", "evilactions.githubusercontent.com"):
            with self.subTest(host=host), mock.patch.dict(os.environ, {
                    "ACTIONS_ID_TOKEN_REQUEST_URL": "https://" + host + "/token",
                    "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "PRIVATE"}, clear=True), \
                 mock.patch.object(entry.urllib.request, "build_opener") as opener:
                with self.assertRaisesRegex(ValueError, "OIDC issuer endpoint"):
                    entry.oidc_token()
                opener.assert_not_called()

    def test_pinned_terraform_archive_member_allowlist(self):
        archive = self.base / "terraform.zip"
        with zipfile.ZipFile(archive, "w") as output:
            output.writestr("LICENSE.txt", "license")
            output.writestr("terraform", "synthetic executable")
        data = archive.read_bytes()
        target = hosted.extract_terraform_archive(data, self.base / "bin",
                                                  hashlib.sha256(data).hexdigest())
        self.assertEqual(target.read_bytes(), b"synthetic executable")
        with zipfile.ZipFile(archive, "a") as output:
            output.writestr("extra", "unreviewed")
        changed = archive.read_bytes()
        with self.assertRaisesRegex(ValueError, "archive-members-invalid"):
            hosted.extract_terraform_archive(changed, self.base / "other-bin",
                                             hashlib.sha256(changed).hexdigest())
        with self.assertRaisesRegex(ValueError, "archive-mismatch"):
            hosted.extract_terraform_archive(changed, self.base / "third-bin",
                                             hashlib.sha256(data).hexdigest())

    def test_hosted_refuses_ambient_backend_and_shell_routing(self):
        for name in ("TF_CLI_ARGS_init", "TF_CLI_CONFIG_FILE", "TF_DATA_DIR", "TF_WORKSPACE",
                     "AWS_ENDPOINT_URL_STS", "AWS_CA_BUNDLE", "AWS_PROFILE", "AWS_ROLE_ARN",
                     "https_proxy", "BASH_FUNC_terraform%%", "BASHOPTS", "GIT_CONFIG_COUNT",
                     "PYTHONUSERBASE", "PYTHONWARNINGS", "PYTHONINSPECT"):
            with self.subTest(name=name), mock.patch.dict(os.environ, {name: "unsafe"}, clear=True):
                with self.assertRaisesRegex(ValueError, "ambient-override-refused"):
                    hosted.reject_runtime_overrides()
        with mock.patch.dict(os.environ, {"AWS_ACCESS_KEY_ID": "synthetic",
                                              "AWS_SECRET_ACCESS_KEY": "synthetic",
                                              "AWS_SESSION_TOKEN": "synthetic",
                                              "AWS_REGION": "us-east-1"}, clear=True):
            hosted.reject_runtime_overrides()

    def test_hosted_request_requires_fixed_private_paths(self):
        stage = self.base / "stage"
        stage.mkdir(mode=0o700)
        backend_role = "arn:aws:iam::123456789012:role/honua-terraform-backend"
        deploy_role = "arn:aws:iam::123456789012:role/honua-cert-deploy"
        hosted_request = {"schema": "honua.aws-cert-plan-request/v2",
                          "sealedStaging": {"mode": "age-v1.3.2-exact-run"},
                          "privateStaging": {"directory": str(stage / "private")},
                          "varFile": str(stage / "terraform.tfvars.json"),
                          "backendTokenFile": str(stage / "backend-token"),
                          "iacRevision": self.revision, "candidateDigest": self.candidate,
                          "expiresInSeconds": 600,
                          "sessionExpiresAtUtc": (datetime.now(timezone.utc) + timedelta(minutes=45)).isoformat(),
                          "accountId": "123456789012", "region": "us-east-1",
                          "backend": {"bucket_id": "honua-existing-state",
                                      "object_key": "standing/cert/terraform.tfstate"},
                          "workloadIdentityContract": {"role_arn": backend_role},
                          "executionIdentityContract": {"roles": {"infra_deployment": {"role_arn": deploy_role}}}}
        backend_bytes = hosted.canonical_backend_source(hosted_request)
        hosted_request["backendSourceSha256"] = hashlib.sha256(backend_bytes).hexdigest()
        (stage / "request.json").write_text(json.dumps(hosted_request))
        env = dict(self.env, CERT_DEPLOY_ROLE_ARN=deploy_role, AWS_REGION="us-east-1")
        with mock.patch.object(hosted, "STAGE", stage), mock.patch.dict(os.environ, env, clear=True):
            self.assertEqual(hosted.fixed_request(), hosted_request)
            hosted.admit_backend_source(hosted_request, backend_bytes)
            with self.assertRaisesRegex(ValueError, "backend-source-refused"):
                hosted.admit_backend_source(hosted_request, backend_bytes + b"endpoint = \"https://evil.invalid\"\n")
            changed = dict(hosted_request, backendSourceSha256=hashlib.sha256(
                backend_bytes + b"endpoint = \"https://evil.invalid\"\n").hexdigest())
            with self.assertRaisesRegex(ValueError, "backend-source-refused"):
                hosted.admit_backend_source(changed, backend_bytes + b"endpoint = \"https://evil.invalid\"\n")
            os.environ["CERT_DEPLOY_ROLE_ARN"] = "arn:aws:iam::123456789012:role/other"
            with self.assertRaisesRegex(ValueError, "hosted-request-invalid"):
                hosted.fixed_request()
            os.environ["CERT_DEPLOY_ROLE_ARN"] = deploy_role
            self.assertEqual(hosted.fixed_request(), hosted_request)
            future_expiry = hosted_request["sessionExpiresAtUtc"]
            hosted_request["sessionExpiresAtUtc"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
            (stage / "request.json").write_text(json.dumps(hosted_request))
            with self.assertRaisesRegex(ValueError, "hosted-request-invalid"):
                hosted.fixed_request()
            hosted_request["sessionExpiresAtUtc"] = future_expiry
            (stage / "request.json").write_text(json.dumps(hosted_request))
            self.assertEqual(hosted.fixed_request(), hosted_request)
            hosted_request["sessionExpiresAtUtc"] = (datetime.now(timezone.utc) + timedelta(hours=2)).isoformat()
            (stage / "request.json").write_text(json.dumps(hosted_request))
            with self.assertRaisesRegex(ValueError, "hosted-request-invalid"):
                hosted.fixed_request()
            hosted_request["sessionExpiresAtUtc"] = future_expiry
            (stage / "request.json").write_text(json.dumps(hosted_request))
            self.assertEqual(hosted.fixed_request(), hosted_request)
            hosted_request["varFile"] = str(self.base / "wrong-file")
            (stage / "request.json").write_text(json.dumps(hosted_request))
            with self.assertRaisesRegex(ValueError, "hosted-request-invalid"):
                hosted.fixed_request()

    def test_sealed_export_rejects_private_or_unbound_receipt(self):
        export = self.base / "encrypted-plan-test"
        export.mkdir(mode=0o700)
        ciphertext = export / "plan.age"
        ciphertext.write_bytes(b"synthetic age ciphertext")
        ciphertext.chmod(0o600)
        producer = {"schema": "honua.aws-cert-plan-public/v1", "status": "planned-unapproved",
                    "iacRevision": self.revision, "candidateDigest": self.candidate}
        expected = {"schema": "honua.aws-cert-plan-envelope/v1",
                    "status": "encrypted-awaiting-local-retention",
                    "context": {"repository": "honua-io/honua-server",
                                "workflow": ".github/workflows/standing-aws-cert-plan.yml",
                                "ref": "refs/heads/trunk", "headSha": self.revision,
                                "runId": 123, "attempt": 1},
                    "producer": producer, "recipientSha256": "c" * 64,
                    "ciphertextSha256": hashlib.sha256(ciphertext.read_bytes()).hexdigest(),
                    "encryptionVersion": "age/v1.3.2", "approvalStatus": "not-approved",
                    "qualificationStatus": "not-evaluated"}
        request = {"sealedStaging": {"recipientSha256": "c" * 64}}
        receipt = export / "receipt.json"
        def write(value):
            receipt.write_bytes(json.dumps(value, sort_keys=True, indent=2).encode() + b"\n")
            receipt.chmod(0o600)
        env = {"GITHUB_SHA": self.revision, "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"}
        with mock.patch.dict(os.environ, env, clear=True):
            write(expected)
            hosted.validate_sealed_export(expected, export, producer, request)
            for changed in ({**expected, "PRIVATE": "SENTINEL"},
                            {**expected, "ciphertextSha256": "d" * 64},
                            {**expected, "context": {**expected["context"], "runId": 124}},
                            {**expected, "producer": {**producer, "PRIVATE": "SENTINEL"}}):
                with self.subTest(changed=changed), self.assertRaises(ValueError):
                    write(changed)
                    hosted.validate_sealed_export(changed, export, producer, request)
            write(expected)
            receipt.write_bytes(b'{"PRIVATE":"SENTINEL",' + receipt.read_bytes()[1:])
            with self.assertRaisesRegex(ValueError, "envelope-export-invalid"):
                hosted.validate_sealed_export(expected, export, producer, request)


if __name__ == "__main__":
    unittest.main()
