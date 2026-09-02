from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "scripts/cloud/certify-lambda-preview.sh").read_text(encoding="utf-8")
WORKFLOW = (ROOT / ".github/workflows/lambda-preview-certification.yml").read_text(encoding="utf-8")


class LambdaPreviewLaneContractTests(unittest.TestCase):
    def test_uses_cert_environment_and_oidc_only(self):
        self.assertIn("environment: cert", WORKFLOW)
        self.assertIn("id-token: write", WORKFLOW)
        self.assertIn("vars.REALAWS_CERT_ROLE_ARN", WORKFLOW)
        self.assertNotIn("AWS_ACCESS_KEY_ID", WORKFLOW + SCRIPT)
        self.assertNotIn("AWS_SECRET_ACCESS_KEY", WORKFLOW + SCRIPT)

    def test_destroy_is_prefix_and_ownership_guarded(self):
        self.assertIn('[[ "$function_name" != honua-certrun-lambda-* ]]', SCRIPT)
        self.assertIn('[[ "$log_group" != /aws/lambda/honua-certrun-lambda-* ]]', SCRIPT)
        self.assertIn('lambda list-tags', SCRIPT)
        self.assertIn('honua-cert-run', SCRIPT)
        self.assertIn('aws lambda delete-function --function-name "$function_name"', SCRIPT)
        self.assertNotIn("delete-repository", SCRIPT)

    def test_receipt_requires_real_digest_response_logs_and_teardown(self):
        for fragment in (
            'ecrDigest:$ecr_digest',
            'runtimeAdapterVerified:true',
            'responseVerified:true',
            'cloudWatchLogsVerified:true',
            'functionDeleted:true',
            'logGroupDeleted:true',
            'result:"pass"',
        ):
            self.assertIn(fragment, SCRIPT)
        self.assertNotIn("pending-ecr-mirror", SCRIPT)

    def test_certifies_the_documented_arm64_lambda_artifact(self):
        self.assertIn('docker pull --platform linux/arm64 "$source_ref"', SCRIPT)
        self.assertIn('source_architecture" != "arm64"', SCRIPT)
        self.assertIn('--architectures arm64', SCRIPT)
        self.assertIn('architecture:"arm64"', SCRIPT)
        self.assertNotIn('linux/amd64', SCRIPT)
        self.assertNotIn('x86_64', SCRIPT)

    def test_supplies_isolated_production_startup_configuration(self):
        self.assertIn('--environment "$cert_environment"', SCRIPT)
        self.assertIn('ConnectionStrings__DefaultConnection:', SCRIPT)
        self.assertIn('HONUA_ADMIN_PASSWORD:$admin_password', SCRIPT)
        self.assertIn('HONUA_SKIP_MIGRATIONS:"true"', SCRIPT)
        self.assertIn('Security__ConnectionEncryption__MasterKey:$master_key', SCRIPT)

    def test_standing_limits_are_preserved_verbatim(self):
        limits = (
            "plan summaries to the evidence thread BEFORE apply, STOP on any destroy beyond the "
            "lane's own teardown-of-what-it-created, no IAM trust widening, fingerprints only."
        )
        normalized = " ".join((SCRIPT + "\n" + WORKFLOW).split())
        self.assertIn(limits, normalized)


if __name__ == "__main__":
    unittest.main()
