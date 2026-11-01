"""Secret-safe regression checks for the shipped quickstart (Docker Compose v2)."""
import hmac
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import urllib.request

from quickstart import initialize


ROOT = Path(__file__).resolve().parents[2]


class QuickstartTests(unittest.TestCase):
    def test_issue_5419_no_redis_keeps_storage_initialization_dependency(self):
        override = (ROOT / "docker-compose.no-redis.yml").read_text()
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

    def render(self, env_file, **overrides):
        env = {key: value for key, value in os.environ.items()
               if key not in ("POSTGRES_PASSWORD", "MINIO_ROOT_PASSWORD", "HONUA_BIND_ADDRESS",
                              "HONUA_CONSOLE_IMAGE", "HONUA_PUBLIC_BASE_URL")}
        env.update(overrides)
        result = subprocess.run(
            ["docker", "compose", "--env-file", str(env_file), "-f",
             str(ROOT / "docker-compose.yml"), "--profile", "minio", "--profile",
             "console", "config", "--format", "json"],
            env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, "Compose must render without disclosing credentials")
        return json.loads(result.stdout)["services"]

    def test_console_default_image_and_binding(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
            image = services["console"]["image"]
            self.assertEqual(image, "ghcr.io/honua-io/honua-console@sha256:b74dfd7ccfd30744e87021b852f287cb81c266b40051ef860314dab33fde2e54")
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
