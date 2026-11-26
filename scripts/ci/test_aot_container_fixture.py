"""Offline checks for the shared Native AOT CI fixture."""

from __future__ import annotations

import unittest
from unittest.mock import Mock, call, patch

import aot_container_fixture as fixture


class AotContainerFixtureTests(unittest.TestCase):
    def test_capped_fixture_uses_shared_seed_and_restart(self) -> None:
        options = fixture.ContainerOptions(
            boot_timeout=90,
            environment=("RateLimiting__Enabled=false",),
            memory_limit_bytes=1024**3,
        )
        lifecycle = Mock()
        with (
            patch.object(fixture, "_run_docker_checked") as docker,
            patch.object(fixture, "_wait_for_postgis") as database,
            patch.object(fixture, "_wait_for_ready") as readiness,
            patch.object(fixture, "_apply_seed_sql") as seed,
        ):
            lifecycle.attach_mock(docker, "docker")
            lifecycle.attach_mock(database, "database")
            lifecycle.attach_mock(readiness, "readiness")
            lifecycle.attach_mock(seed, "seed")
            session = fixture.start_container_stack("server-image", "postgis-image", options)

        network, postgres, app, stop, start = [entry.args[0] for entry in docker.call_args_list]
        self.assertEqual(network, ["network", "create", session.network])
        self.assertEqual(postgres[-1], "postgis-image")
        self.assertEqual(app[-1], "server-image")
        self.assertIn("--memory=1073741824", app)
        self.assertIn("--memory-swap=1073741824", app)
        self.assertIn("RateLimiting__Enabled=false", app)
        self.assertEqual(stop, ["stop", session.app_container])
        self.assertEqual(start, ["start", session.app_container])
        self.assertEqual(lifecycle.mock_calls, [
            call.docker(network),
            call.docker(postgres),
            call.database(session.pg_container, 90),
            call.docker(app),
            call.readiness(session.base_url, 90),
            call.docker(stop),
            *(call.seed(session.pg_container, path) for path in fixture.SEED_SQL_PATHS),
            call.docker(start),
            call.readiness(session.base_url, 90),
        ])

    def test_interrupted_start_removes_the_containers_and_network(self) -> None:
        with (
            patch.object(fixture, "_run_docker_checked") as docker,
            patch.object(fixture, "_wait_for_postgis", side_effect=KeyboardInterrupt),
            patch.object(fixture, "stop_container_stack") as cleanup,
            self.assertRaises(KeyboardInterrupt),
        ):
            fixture.start_container_stack("server-image", "postgis-image", fixture.ContainerOptions(90))

        self.assertEqual(docker.call_count, 2)
        cleanup.assert_called_once()
        self.assertEqual(cleanup.call_args.args[0].network, docker.call_args_list[0].args[0][-1])
