# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Honua server process management for integration tests.

Provides:
- Starting/stopping the Honua server as a subprocess
- Configuration for test environment (connection string, auth bypass)
- Health check waiting
"""

from __future__ import annotations

import os
import signal
import subprocess
import sys
import time
from pathlib import Path


class HonuaServer:
    """
    Manages the Honua server process for integration tests.

    Starts the server as a subprocess with test configuration,
    waits for it to be healthy, and provides the base URL for tests.
    """

    # Default port for test server
    DEFAULT_PORT = 5555

    def __init__(
        self,
        connection_string: str,
        port: int = DEFAULT_PORT,
        project_root: Path | None = None,
    ):
        """
        Initialize the server manager.

        Args:
            connection_string: PostgreSQL connection string for the test database
            port: Port to run the server on
            project_root: Path to the Honua project root (auto-detected if None)
        """
        self.connection_string = connection_string
        self.port = port
        self.project_root = project_root or self._find_project_root()
        self._process: subprocess.Popen | None = None

    @property
    def base_url(self) -> str:
        """Get the base URL for the running server."""
        return f"http://localhost:{self.port}"

    def _find_project_root(self) -> Path:
        """Find the Honua project root directory."""
        # Start from current file and traverse up to find solution file
        current = Path(__file__).resolve()
        for parent in current.parents:
            if (parent / "Honua.sln").exists():
                return parent
        # Fallback to cwd
        return Path.cwd()

    def start(self, timeout: float = 60.0) -> "HonuaServer":
        """
        Start the Honua server and wait for it to be healthy.

        Args:
            timeout: Maximum seconds to wait for server to start

        Returns:
            Self for chaining

        Raises:
            RuntimeError: If server fails to start within timeout
        """
        server_project = self.project_root / "src" / "Honua.Server"

        if not server_project.exists():
            raise RuntimeError(f"Server project not found at {server_project}")

        env = os.environ.copy()
        env.update({
            "ASPNETCORE_URLS": f"http://localhost:{self.port}",
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ConnectionStrings__HonuaDb": self.connection_string,
            # Enable dev auth bypass for tests
            "HONUA_DEV_AUTH": "true",
            # Disable HTTPS redirection for tests
            "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "false",
        })

        # Start the server process
        self._process = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", str(server_project)],
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=str(self.project_root),
        )

        # Wait for server to be healthy
        self._wait_for_health(timeout)

        return self

    def _wait_for_health(self, timeout: float):
        """Wait for the server to respond to health checks."""
        import httpx

        health_url = f"{self.base_url}/health"
        start_time = time.time()

        while time.time() - start_time < timeout:
            try:
                response = httpx.get(health_url, timeout=2.0)
                if response.status_code == 200:
                    return
            except httpx.RequestError:
                pass

            # Check if process died
            if self._process and self._process.poll() is not None:
                stdout, stderr = self._process.communicate()
                raise RuntimeError(
                    f"Server process died unexpectedly.\n"
                    f"stdout: {stdout.decode()}\n"
                    f"stderr: {stderr.decode()}"
                )

            time.sleep(0.5)

        raise RuntimeError(f"Server did not become healthy within {timeout}s")

    def stop(self):
        """Stop the server process."""
        if self._process:
            # Try graceful shutdown first
            self._process.terminate()
            try:
                self._process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                # Force kill if graceful shutdown fails
                self._process.kill()
                self._process.wait()
            self._process = None

    def __enter__(self) -> "HonuaServer":
        return self.start()

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.stop()


class HonuaServerFixture:
    """
    Pytest fixture-friendly wrapper for HonuaServer.

    Manages both PostGIS and Honua server lifecycle together.
    """

    def __init__(self, postgis_fixture: "PostGISFixture", port: int = 5555):
        from .postgis import PostGISFixture

        self.postgis = postgis_fixture
        self.port = port
        self._server: HonuaServer | None = None

    @property
    def base_url(self) -> str:
        """Get the server base URL."""
        if not self._server:
            raise RuntimeError("Server not started")
        return self._server.base_url

    @property
    def connection_string(self) -> str:
        """Get the database connection string."""
        return self.postgis.connection_string

    def start(self) -> "HonuaServerFixture":
        """Start the Honua server."""
        self._server = HonuaServer(
            connection_string=self.postgis.connection_string,
            port=self.port,
        )
        self._server.start()
        return self

    def stop(self):
        """Stop the server."""
        if self._server:
            self._server.stop()
            self._server = None

    def __enter__(self) -> "HonuaServerFixture":
        return self.start()

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.stop()
