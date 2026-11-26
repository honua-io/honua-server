"""Offline checks for the shared Native AOT CI fixture."""

from __future__ import annotations

import unittest
from unittest.mock import patch

import aot_container_fixture as fixture


class AotContainerFixtureTests(unittest.TestCase):
    def test_capped_fixture_uses_shared_seed_and_restart(self) -> None:
        options = fixture.ContainerOptions(
            environment=("RateLimiting__Enabled=false",),
            memory_limit_bytes=1024**3,
        )
        with (
            patch.object(fixture, "_run_docker_checked") as docker,
            patch.object(fixture, "_wait_for_postgis"),
            patch.object(fixture, "_wait_for_ready") as readiness,
            patch.object(fixture, "_apply_seed_sql") as seed,
        ):
            session = fixture.start_container_stack("server-image", "postgis-image", 90, options=options)

        network, postgres, app, stop, start = [entry.args[0] for entry in docker.call_args_list]
        self.assertEqual(network, ["network", "create", session.network])
        self.assertEqual(postgres[-1], "postgis-image")
        self.assertEqual(app[-1], "server-image")
        self.assertIn("--memory=1073741824", app)
        self.assertIn("--memory-swap=1073741824", app)
        self.assertIn("RateLimiting__Enabled=false", app)
        self.assertEqual(stop, ["stop", session.app_container])
        self.assertEqual(start, ["start", session.app_container])
        self.assertEqual(
            [entry.args for entry in seed.call_args_list],
            [(session.pg_container, path) for path in fixture.SEED_SQL_PATHS],
        )
        self.assertEqual([entry.args for entry in readiness.call_args_list], [(session.base_url, 90)] * 2)
