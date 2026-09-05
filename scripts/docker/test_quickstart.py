"""Secret-safe regression checks for the shipped quickstart (Docker Compose v2)."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from quickstart import initialize


ROOT = Path(__file__).resolve().parents[2]


class QuickstartTests(unittest.TestCase):
    def test_existing_settings_survive_and_weak_credentials_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            env_file = Path(directory) / ".env"
            env_file.write_text("HONUA_HTTP_PORT=18080\n")
            initialize(env_file)
            self.assertTrue(env_file.read_text().startswith("HONUA_HTTP_PORT=18080\n"))
            env_file.write_text("POSTGRES_PASSWORD=\n")
            with self.assertRaises(ValueError):
                initialize(env_file)
            self.assertTrue(env_file.read_text() == "POSTGRES_PASSWORD=\n")

    def render(self, env_file, **overrides):
        env = {key: value for key, value in os.environ.items()
               if key not in ("POSTGRES_PASSWORD", "MINIO_ROOT_PASSWORD", "HONUA_BIND_ADDRESS")}
        env.update(overrides)
        result = subprocess.run(
            ["docker", "compose", "--env-file", str(env_file), "-f",
             str(ROOT / "docker-compose.yml"), "--profile", "minio", "--profile",
             "console", "config", "--format", "json"],
            env=env, capture_output=True, text=True)
        self.assertTrue(result.returncode == 0, "Compose must render without disclosing credentials")
        return json.loads(result.stdout)["services"]

    def test_all_profile_ports_are_loopback_by_default(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
            for name, service in services.items():
                for port in service.get("ports", []):
                    self.assertTrue(port.get("host_ip") == "127.0.0.1", name)

    def test_explicit_bind_address_is_respected(self):
        with tempfile.NamedTemporaryFile() as env_file:
            services = self.render(env_file.name, HONUA_BIND_ADDRESS="192.0.2.10",
                                   POSTGRES_PASSWORD=os.urandom(32).hex(),
                                   MINIO_ROOT_PASSWORD=os.urandom(32).hex())
            for name, service in services.items():
                for port in service.get("ports", []):
                    self.assertTrue(port.get("host_ip") == "192.0.2.10", name)

    def test_passwords_are_unique_persistent_and_wired_to_server(self):
        passwords = []
        with tempfile.TemporaryDirectory() as directory:
            for index in range(2):
                env_file = Path(directory) / str(index)
                command = ["python3", str(ROOT / "scripts/docker/quickstart.py"),
                           "--env-file", str(env_file), "--init-only"]
                result = subprocess.run(command, capture_output=True)
                self.assertTrue(result.returncode == 0, "Credential initialization must succeed")
                original = env_file.read_bytes()
                result = subprocess.run(command, capture_output=True)
                self.assertTrue(result.returncode == 0)
                self.assertTrue(original == env_file.read_bytes(), "Restart must preserve credentials")
                self.assertTrue(env_file.stat().st_mode & 0o077 == 0)
                services = self.render(env_file)
                password = services["postgres"]["environment"]["POSTGRES_PASSWORD"]
                minio = services["minio"]["environment"]["MINIO_ROOT_PASSWORD"]
                self.assertTrue(len(password) >= 32 and len(minio) >= 32)
                self.assertTrue(password != minio)
                connection = services["honua"]["environment"]["ConnectionStrings__DefaultConnection"]
                self.assertTrue(connection.endswith("Password=" + password))
                passwords.append(password)
            self.assertTrue(passwords[0] != passwords[1], "Installs must not share a password")

    def test_missing_password_fails_closed(self):
        with tempfile.NamedTemporaryFile() as env_file:
            result = subprocess.run(
                ["docker", "compose", "--env-file", env_file.name, "-f",
                 str(ROOT / "docker-compose.yml"), "config", "--quiet"],
                env={key: value for key, value in os.environ.items()
                     if key not in ("POSTGRES_PASSWORD", "MINIO_ROOT_PASSWORD")},
                capture_output=True)
            self.assertTrue(result.returncode != 0, "Missing credentials must refuse startup")


if __name__ == "__main__":
    unittest.main()
