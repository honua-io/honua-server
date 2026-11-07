"""The GP integration profile must keep its workers, artifact volume, and source mount."""

import copy
import importlib.util
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/check-gp-integration-profile.py"
COMPOSE = ROOT / "docker/compose/docker-compose.gp-dev.yml"


def load_checker():
    spec = importlib.util.spec_from_file_location("check_gp_integration_profile", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CHECKER = load_checker()


class IntegrationProfileTests(unittest.TestCase):
    def test_repository_compose_file_defines_the_profile(self):
        document = yaml.safe_load(COMPOSE.read_text(encoding="utf-8"))
        self.assertEqual([], CHECKER.source_problems(document))

    def test_rendered_profile_config_when_docker_is_available(self):
        if not CHECKER.docker_available():
            self.skipTest("docker is not installed")
        problems = CHECKER.check(COMPOSE, use_docker=True)
        self.assertEqual([], problems)
        services = CHECKER.compose_config(COMPOSE, CHECKER.PROFILE)["services"]
        for name in ("honua", *CHECKER.WORKERS):
            self.assertEqual(str(ROOT), services[name]["build"]["context"])
        database_mount = next(mount for mount in services["postgres"]["volumes"]
                              if mount["target"] == "/docker-entrypoint-initdb.d/init-db.sql")
        self.assertEqual(str(ROOT / "docker/init-db.sql"), database_mount["source"])

    def test_missing_profile_worker_volume_or_mount_fails(self):
        document = yaml.safe_load(COMPOSE.read_text(encoding="utf-8"))
        cases = {
            "profile": lambda doc: doc["services"]["managed-worker"].__setitem__("profiles", ["other"]),
            "managed worker": lambda doc: doc["services"].pop("managed-worker"),
            "native worker": lambda doc: doc["services"].pop("gdal-worker"),
            "artifact volume": lambda doc: doc["volumes"].pop("gp_artifacts"),
            "source mount": lambda doc: doc["services"]["gdal-worker"].__setitem__(
                "volumes", ["gp_artifacts:/var/lib/honua/gp-artifacts"]),
        }
        for name, mutate in cases.items():
            with self.subTest(name=name):
                sample = copy.deepcopy(document)
                mutate(sample)
                self.assertNotEqual([], CHECKER.source_problems(sample), name)


if __name__ == "__main__":
    unittest.main()
