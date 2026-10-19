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
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).with_name("standing-cert-plan-only.py")
spec = importlib.util.spec_from_file_location("standing_cert_plan_only", SCRIPT)
entry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(entry)


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


if __name__ == "__main__":
    unittest.main()
