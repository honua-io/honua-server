"""Secret-safe regression checks for the shipped quickstart (Docker Compose v2)."""
import base64
import hmac
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import urllib.request

from quickstart import initialize


ROOT = Path(__file__).resolve().parents[2]


class QuickstartTests(unittest.TestCase):
    def test_materialization_timeout_is_forwarded_from_dotenv(self):
        with tempfile.TemporaryDirectory() as directory:
            env_file = Path(directory) / '.env'
            env_file.write_text('LayerPublishing__MaterializationTimeoutSeconds=900\n')
            services = self.render(env_file, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())

            self.assertEqual('900', services['honua']['environment'].get('LayerPublishing__MaterializationTimeoutSeconds'))

    def test_issue_5419_no_redis_keeps_storage_initialization_dependency(self):
        override = (ROOT / "docker/compose/docker-compose.no-redis.yml").read_text()
        depends_on = override.split("depends_on: !override", 1)[1].split("environment:", 1)[0]

        self.assertIn("storage-init:", depends_on)
        self.assertIn("condition: service_completed_successfully", depends_on)

    def test_demo_callers_initialize_before_compose_and_use_container_credentials(self):
        for name in ("stac-ops", "mobile-offline"):
            source = (ROOT / f"scripts/demos/run-{name}-demo.sh").read_text()
            self.assertLess(source.index('quickstart.py" --init-only'), source.index("compose down"))
            self.assertIn('PGPASSWORD="${PGPASSWORD:-$POSTGRES_PASSWORD}"', source)
            self.assertNotIn("honua_password", source)

    def test_existing_settings_survive_and_weak_credentials_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            env_file = Path(directory) / ".env"
            env_file.write_text("HONUA_HTTP_PORT=18080\n")
            initialize(env_file)
            self.assertTrue(env_file.read_text().startswith("HONUA_HTTP_PORT=18080\n"))
            env_file.write_text("POSTGRES_PASSWORD=\n")
            with self.assertRaises(ValueError):
                initialize(env_file)
            self.assertTrue(hmac.compare_digest(env_file.read_text(), "POSTGRES_PASSWORD=\n"))

    def test_audit_key_is_private_persistent_unique_and_wired_to_server(self):
        keys = []
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
            for index in range(2):
                env_file = Path(directory) / str(index)
                result = subprocess.run(
                    ["python3", str(ROOT / "scripts/docker/quickstart.py"),
                     "--env-file", str(env_file), "--init-only"], capture_output=True)
                self.assertEqual(result.returncode, 0)
                self.assertEqual(result.stdout + result.stderr, b"")
                original = env_file.read_bytes()
                initialize(env_file)
                self.assertTrue(hmac.compare_digest(original, env_file.read_bytes()))
                self.assertEqual(env_file.stat().st_mode & 0o077, 0)
                key = self.render(env_file)["honua"]["environment"]["AuditLog__ChainVerification__Key"]
                self.assertTrue(len(base64.b64decode(key, validate=True)) >= 32)
                keys.append(key)
            self.assertFalse(hmac.compare_digest(*keys))

    def test_existing_password_file_gains_audit_key_without_rotating_passwords(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
            env_file = Path(directory) / ".env"
            existing = "POSTGRES_PASSWORD=" + "a" * 64 + "\nMINIO_ROOT_PASSWORD=" + "b" * 64 + "\n"
            env_file.write_text(existing)
            initialize(env_file)
            self.assertTrue(env_file.read_text().startswith(existing))
            self.assertIn("AuditLog__ChainVerification__Key=", env_file.read_text())

    def test_copied_environment_template_initializes(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
            env_file = Path(directory) / ".env"
            env_file.write_text((ROOT / ".env.docker.example").read_text())
            initialize(env_file)
            key = self.render(env_file)["honua"]["environment"]["AuditLog__ChainVerification__Key"]
            self.assertGreaterEqual(len(base64.b64decode(key, validate=True)), 32)

    def test_invalid_audit_keys_are_rejected_without_changing_saved_secrets(self):
        for value in ("", "not-base64!", base64.b64encode(b"short").decode()):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
                env_file = Path(directory) / ".env"
                original = "AuditLog__ChainVerification__Key=" + value + "\n"
                env_file.write_text(original)
                with self.assertRaisesRegex(ValueError, "AuditLog__ChainVerification__Key"):
                    initialize(env_file)
                self.assertTrue(hmac.compare_digest(original, env_file.read_text()))

    def test_supplied_audit_key_is_preserved_and_conflicts_are_rejected(self):
        key = base64.b64encode(os.urandom(32)).decode()
        with tempfile.TemporaryDirectory() as directory, patch.dict(
                os.environ, {"AuditLog__ChainVerification__Key": key}, clear=True):
            env_file = Path(directory) / ".env"
            initialize(env_file)
            self.assertIn("AuditLog__ChainVerification__Key=" + key, env_file.read_text())
            original = env_file.read_bytes()
            with patch.dict(os.environ, {"AuditLog__ChainVerification__Key": base64.b64encode(os.urandom(32)).decode()}):
                with self.assertRaisesRegex(ValueError, "differs"):
                    initialize(env_file)
            self.assertTrue(hmac.compare_digest(original, env_file.read_bytes()))

    def test_smoke_rejects_unhealthy_degraded_empty_and_missing_audit_checks(self):
        source = (ROOT / "scripts/ci/smoke-quickstart-console.sh").read_text()
        self.assertIn("assert_all_health_checks() {", source)
        function = source.split("assert_all_health_checks() {", 1)[1].split("\n}\n", 1)[0]
        healthy = {"status": "Healthy", "entries": {"audit-chain-integrity": {
            "status": "Healthy", "data": {"last_verified_at": "2026-11-09T19:00:00Z"}}}}
        reports = [healthy,
                   {"status": "Healthy", "entries": {"audit-chain-integrity": {"status": "Healthy",
                       "data": {"last_verified_at": "2026-11-09T18:59:59Z"}}}}, {**healthy, "status": "Unhealthy"},
                   {"status": "Healthy", "entries": {"audit-chain-integrity": {"status": "Healthy"}}},
                   {"status": "Healthy", "entries": {}},
                   {"status": "Healthy", "entries": {"database": {"status": "Healthy"}}}]
        for status in ("Unhealthy", "Degraded"):
            reports.append({"status": "Healthy", "entries": {
                "audit-chain-integrity": {"status": "Healthy"}, "database": {"status": status}}})
        with tempfile.TemporaryDirectory() as directory:
            report_file = Path(directory) / "health.json"
            for index, report in enumerate(reports):
                report_file.write_text(json.dumps(report))
                result = subprocess.run(["bash", "-c",
                    'set -euo pipefail; curl() { cat "$REPORT_FILE"; }; '
                    'HONUA_HTTP_PORT=18080; ADMIN_PASSWORD=unused; '
                    'AUDIT_VERIFIED_AFTER=2026-11-09T18:59:59Z; sleep() { :; }; '
                    'assert_all_health_checks() {' + function + '\n}; assert_all_health_checks 1'],
                    env={**os.environ, "REPORT_FILE": str(report_file)}, capture_output=True)
                self.assertEqual(result.returncode == 0, index == 0, "Smoke must reject unhealthy or incomplete health")

    def render(self, env_file, **overrides):
        env = {key: value for key, value in os.environ.items()
               if key not in ("POSTGRES_PASSWORD", "MINIO_ROOT_PASSWORD", "HONUA_BIND_ADDRESS",
                              "HONUA_CONSOLE_IMAGE", "HONUA_PUBLIC_BASE_URL", "AuditLog__ChainVerification__Key",
                              "LayerPublishing__MaterializationTimeoutSeconds")}
        env.update(overrides)
        result = subprocess.run(
            ["docker", "compose", "--env-file", str(env_file), "-f",
             str(ROOT / "docker-compose.yml"), "--profile", "minio", "--profile",
             "console", "config", "--format", "json"],
            env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, "Compose must render without disclosing credentials")
        return json.loads(result.stdout)["services"]

    def test_console_healthcheck_reads_the_file_the_image_ships(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
        check = ' '.join(services['console']['healthcheck']['test'])
        # Current Console images ship /app/version.json (served at GET /version.json).
        self.assertIn('test -s /app/version.json', check)

    def test_workflow_view_default_is_forwarded_to_server(self):
        with tempfile.NamedTemporaryFile() as env_file:
            for selected, expected in (('', 'default'), ('configure', 'configure')):
                with self.subTest(selected=selected):
                    services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                           MINIO_ROOT_PASSWORD=os.urandom(32).hex(),
                                           HONUA_MCP_DEFAULT_VIEW=selected)
                    self.assertEqual(expected, services['honua']['environment'].get('Mcp__WorkflowViews__DefaultView'))

    def test_console_default_image_and_binding(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
            image = services["console"]["image"]
            self.assertEqual(image, "ghcr.io/honua-io/honua-console@sha256:ab453db9b2a468a05e58ebc445569fd2df84908df91967ae8d31417d6e971358")
            self.assertEqual(services["console"]["environment"]["HONUA_SERVER_BASE_URL"], "http://honua:8080")
            self.assertEqual(services["honua"]["environment"]["PUBLIC_BASE_URL"], "http://localhost:8080")

    def test_console_custom_port_and_oidc_provider_are_wired(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex(), HONUA_CONSOLE_PORT="15174",
                                   HONUA_PUBLIC_BASE_URL="http://localhost:15174",
                                   OIDC_ENABLED="true", OIDC_GENERIC_ENABLED="true",
                                   OIDC_GENERIC_AUTHORITY="https://idp.example.com",
                                   OIDC_GENERIC_CLIENT_ID="console", OIDC_GENERIC_CLIENT_SECRET="test-secret")
            env = services["honua"]["environment"]
            self.assertEqual(env["PUBLIC_BASE_URL"], "http://localhost:15174")
            self.assertEqual(env["Oidc__Enabled"], "true")
            self.assertEqual(env["Oidc__Generic__Enabled"], "true")
            self.assertEqual(env["Oidc__Generic__Authority"], "https://idp.example.com")
            self.assertEqual(env["Oidc__Generic__ClientId"], "console")
            self.assertEqual(env["Oidc__Generic__ClientSecret"], "test-secret")

    def test_console_profile_enables_studio_without_ai_overlay(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
        self.assertEqual(services["console"]["environment"]["Studio__EmbedJsStudio"], "true")
        self.assertEqual(services["honua"]["environment"]["Studio__EndUserAuthorization__Enabled"], "true")
        self.assertNotIn("StudioAiProxy__Enabled", services["honua"]["environment"])
        self.assertNotIn("AiExecutionAdmission__DeploymentId", services["honua"]["environment"])

    def test_studio_ai_overlay_only_adds_assistant_settings(self):
        overlay = (ROOT / "docker/compose/docker-compose.studio-ai.yml").read_text()
        self.assertNotIn("Studio__EmbedJsStudio", overlay)
        self.assertNotIn("Studio__EndUserAuthorization__Enabled", overlay)
        self.assertIn('StudioAiProxy__Enabled: "true"', overlay)

    @unittest.skipUnless(os.environ.get("HONUA_VERIFY_CONSOLE_REGISTRY") == "1", "opt-in public registry check")
    def test_console_default_image_is_publicly_pullable(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
        digest = services["console"]["image"].split("@", 1)[1]
        repo = "honua-io/honua-console"
        with urllib.request.urlopen(f"https://ghcr.io/token?scope=repository:{repo}:pull", timeout=30) as response:
            token = json.load(response)["token"]
        request = urllib.request.Request(f"https://ghcr.io/v2/{repo}/manifests/{digest}", headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json",
        })
        with urllib.request.urlopen(request, timeout=30) as response:
            self.assertEqual(response.status, 200)
            self.assertEqual(response.headers["Docker-Content-Digest"], digest)

    def test_all_profile_ports_are_loopback_by_default(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
            for name, service in services.items():
                for port in service.get("ports", []):
                    self.assertEqual(port.get("host_ip"), "127.0.0.1", name)

    def test_explicit_bind_address_is_respected(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, HONUA_BIND_ADDRESS="192.0.2.10",
                                   POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
            for name, service in services.items():
                for port in service.get("ports", []):
                    self.assertEqual(port.get("host_ip"), "192.0.2.10", name)

    def test_passwords_are_unique_persistent_and_wired_to_server(self):
        passwords = []
        with tempfile.TemporaryDirectory() as directory:
            for index in range(2):
                env_file = Path(directory) / str(index)
                command = ["python3", str(ROOT / "scripts/docker/quickstart.py"),
                           "--env-file", str(env_file), "--init-only"]
                result = subprocess.run(command, capture_output=True)
                self.assertEqual(result.returncode, 0, "Credential initialization must succeed")
                original = env_file.read_bytes()
                result = subprocess.run(command, capture_output=True)
                self.assertEqual(result.returncode, 0)
                self.assertTrue(hmac.compare_digest(original, env_file.read_bytes()), "Restart must preserve credentials")
                self.assertEqual(env_file.stat().st_mode & 0o077, 0)
                services = self.render(env_file)
                password = services["postgres"]["environment"]["POSTGRES_PASSWORD"]
                minio = services["minio"]["environment"]["MINIO_ROOT_PASSWORD"]
                self.assertTrue(len(password) >= 32 and len(minio) >= 32)
                self.assertFalse(hmac.compare_digest(password, minio))
                connection = services["honua"]["environment"]["ConnectionStrings__DefaultConnection"]
                self.assertTrue(connection.endswith("Password=" + password))
                passwords.append(password)
            self.assertFalse(hmac.compare_digest(passwords[0], passwords[1]), "Installs must not share a password")

    def test_missing_password_fails_closed(self):
        with tempfile.NamedTemporaryFile() as env_file:
            result = subprocess.run(
                ["docker", "compose", "--env-file", env_file.name, "-f",
                 str(ROOT / "docker-compose.yml"), "config", "--quiet"],
                env={key: value for key, value in os.environ.items()
                     if key not in ("POSTGRES_PASSWORD", "MINIO_ROOT_PASSWORD")},
                capture_output=True)
            self.assertNotEqual(result.returncode, 0, "Missing credentials must refuse startup")


if __name__ == "__main__":
    unittest.main()
