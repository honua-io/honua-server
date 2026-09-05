"""Render the exact documented production install without logging secret values."""
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class ProductionComposeTests(unittest.TestCase):
    def setUp(self):
        self.document = (ROOT / "docs/guides/deploy/docker-compose.md").read_text()
        self.compose = re.search(r"cat > docker-compose.yml <<'EOF'\n(.*?)\nEOF", self.document, re.S).group(1)
        self.values = {
            "HONUA_IMAGE": "ghcr.io/honua-io/honua-server@sha256:" + "a" * 64,
            "HONUA_HOST": "honua.example.com",
            "HONUA_ADMIN_PASSWORD": secrets.token_hex(32),
            "POSTGRES_PASSWORD": secrets.token_hex(32),
            "HONUA_MASTER_KEY": secrets.token_hex(32),
            "HONUA_CORS_ORIGIN": "https://app.example.com",
            "HONUA_STORAGE_VOLUME_NAME": "production-render-only",
        }

    def render(self, **overrides):
        with tempfile.TemporaryDirectory() as directory:
            compose = Path(directory) / "compose.yml"
            compose.write_text(self.compose)
            values = self.values | overrides
            environment = {key: value for key, value in os.environ.items()
                           if not (key.startswith(("HONUA_", "ConnectionStrings__", "Security__", "Cors__"))
                                   or key == "POSTGRES_PASSWORD")}
            environment.update(values)
            result = subprocess.run(["docker", "compose", "-f", str(compose), "config", "--format", "json"],
                                    env=environment, capture_output=True, text=True)
            return result

    def model(self, **overrides):
        result = self.render(**overrides)
        self.assertEqual(result.returncode, 0, "Production Compose must render")
        return json.loads(result.stdout)

    def test_document_and_shipped_production_file_agree(self):
        source = (ROOT / "docker-compose.production.yml").read_text().strip()
        self.assertTrue(hmac.compare_digest(self.compose.strip(), source))

    def test_proxy_hostname_is_trusted_and_public_base_url_is_explicit(self):
        environment = self.model()["services"]["honua"]["environment"]
        self.assertIn(self.values["HONUA_HOST"], environment.get("AllowedHosts", "").split(";"))
        self.assertEqual(environment.get("PUBLIC_BASE_URL"), "https://honua.example.com")

    def test_postgis_is_initialized_before_final_database_health(self):
        model = self.model()
        postgres = model["services"]["postgres"]
        configs = postgres.get("configs", [])
        self.assertTrue(any(item["target"].startswith("/docker-entrypoint-initdb.d/") for item in configs))
        self.assertIn("CREATE EXTENSION IF NOT EXISTS postgis;", model["configs"]["postgis_init"]["content"])
        self.assertIn("postmaster.pid", postgres["healthcheck"]["test"][1])

    def test_production_env_file_routes_to_production_compose(self):
        guide = (ROOT / "docs/guides/deploy/configuration.md").read_text()
        self.assertIn("docker compose -f docker-compose.production.yml --env-file .env.production up", guide)
        self.assertIn("ASPNETCORE_ENVIRONMENT=Production", (ROOT / ".env.production.example").read_text())

    def test_runtime_secret_overrides_are_honored_in_production(self):
        values = {
            "ConnectionStrings__DefaultConnection": "Host=private-db;Password=" + secrets.token_hex(32),
            "ConnectionStrings__Redis": "private-redis:6379,password=" + secrets.token_hex(32),
            "Security__ConnectionEncryption__MasterKey": secrets.token_hex(32),
        }
        environment = self.model(**values)["services"]["honua"]["environment"]
        self.assertEqual(environment["ASPNETCORE_ENVIRONMENT"], "Production")
        for key, value in values.items():
            self.assertTrue(hmac.compare_digest(environment[key], value), key)
        self.assertEqual(environment["Database__MigrationSafety__ContractApplyPolicy"], "Gate")

    def test_documented_env_file_command_uses_private_runtime_values(self):
        values = self.values | {
            "ConnectionStrings__DefaultConnection": "Host=private-db;Password=" + secrets.token_hex(32),
            "ConnectionStrings__Redis": "private-redis:6379,password=" + secrets.token_hex(32),
            "Security__ConnectionEncryption__MasterKey": secrets.token_hex(32),
        }
        values.pop("HONUA_MASTER_KEY")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "docker-compose.production.yml").write_text(self.compose)
            (root / ".env.production").write_text("\n".join(f"{key}={value}" for key, value in values.items()))
            result = subprocess.run(
                ["docker", "compose", "-f", "docker-compose.production.yml", "--env-file", ".env.production",
                 "config", "--format", "json"], cwd=root,
                env={key: value for key, value in os.environ.items()
                     if key not in values and not key.startswith("COMPOSE_")}, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, "Documented production env-file command must render")
            environment = json.loads(result.stdout)["services"]["honua"]["environment"]
            self.assertEqual(environment["ASPNETCORE_ENVIRONMENT"], "Production")
            for key in ("ConnectionStrings__DefaultConnection", "ConnectionStrings__Redis",
                        "Security__ConnectionEncryption__MasterKey", "HONUA_ADMIN_PASSWORD"):
                self.assertTrue(hmac.compare_digest(environment[key], values[key]), key)

    def test_missing_required_inputs_fail_before_container_creation(self):
        for key in ("HONUA_IMAGE", "HONUA_HOST", "POSTGRES_PASSWORD", "HONUA_ADMIN_PASSWORD", "HONUA_MASTER_KEY"):
            self.assertNotEqual(self.render(**{key: ""}).returncode, 0, key)

    def test_datastores_are_unpublished_and_api_is_loopback_only(self):
        services = self.model()["services"]
        self.assertFalse(services["postgres"].get("ports"))
        self.assertFalse(services["redis"].get("ports"))
        for port in services["honua"]["ports"]:
            self.assertEqual(port["host_ip"], "127.0.0.1")


if __name__ == "__main__":
    unittest.main()
