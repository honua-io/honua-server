"""The published journey must use interactive approval, without injected sessions."""
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock

from lifecycle_human_approval import HumanApprovalClient
from lifecycle_identity_provider import IdentityProvider, base64url, prepare_identity_files


class InteractiveApprovalTests(unittest.TestCase):
    def test_review_is_bound_to_the_returned_proposal_before_submission(self):
        client = HumanApprovalClient("https://localhost:8443", Mock())
        client.request = Mock(return_value={
            "proposal": {"proposalId": "another", "version": 2, "planHash": "plan"},
            "review": {"proposalId": "wanted", "proposalVersion": 2, "planHash": "plan", "capability": "private"}})
        with self.assertRaisesRegex(AssertionError, "review binding"):
            client.approve("wanted")
        self.assertEqual(1, client.request.call_count)

    def test_approval_uses_private_review_capability_and_same_session(self):
        client = HumanApprovalClient("https://localhost:8443", Mock())
        client.request = Mock(side_effect=[{
            "proposal": {"proposalId": "wanted", "version": 2, "planHash": "plan"},
            "review": {"proposalId": "wanted", "proposalVersion": 2, "planHash": "plan", "capability": "private"}}, {}])
        client.approve("wanted")
        self.assertEqual(("/api/v1/admin/proposals/wanted/approve", {}), client.request.call_args.args)
        self.assertEqual({"X-Honua-Approval-Capability": "private"}, client.request.call_args.kwargs["headers"])


class IdentityProviderTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        prepare_identity_files(self.root)
        self.provider = IdentityProvider(self.root, "https://lifecycle-identity:8443")
        self.verifier = "pkce-verifier"
        self.authorization = {
            "response_type": "code", "client_id": "lifecycle-console", "state": "state",
            "redirect_uri": "https://localhost:8443/admin/auth/callback",
            "code_challenge_method": "S256",
            "code_challenge": base64url(hashlib.sha256(self.verifier.encode()).digest())}

    def token(self, code, verifier):
        return self.provider.exchange({"grant_type": "authorization_code", "client_id": "lifecycle-console",
            "redirect_uri": self.authorization["redirect_uri"], "code": code, "code_verifier": verifier})

    def test_wrong_pkce_is_refused_and_consumes_the_code(self):
        code = self.provider.authorize(self.authorization)
        with self.assertRaises(ValueError):
            self.token(code, "wrong")
        with self.assertRaises(ValueError):
            self.token(code, self.verifier)

    def test_code_cannot_be_replayed_and_id_token_is_signed_for_the_client(self):
        code = self.provider.authorize(self.authorization)
        tokens = self.token(code, self.verifier)
        import base64
        header, payload, signature = tokens["id_token"].split(".")
        claims = json.loads(base64.urlsafe_b64decode(payload + "=="))
        self.assertEqual("lifecycle-console", claims["aud"])
        self.assertEqual(self.provider.issuer, claims["iss"])
        self.assertEqual(["admin"], claims["roles"])
        self.assertTrue(signature)
        with self.assertRaises(ValueError):
            self.token(code, self.verifier)


if __name__ == "__main__":
    unittest.main()
