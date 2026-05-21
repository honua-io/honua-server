# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Honua server process management for integration tests.

Provides:
- Starting/stopping the Honua server as a subprocess
- Configuration for test environment (connection string, auth key)
- Health check waiting
"""

from __future__ import annotations

import os
import signal
import subprocess
import sys
import time
from collections import deque
from threading import Thread
from pathlib import Path
from urllib.parse import urlparse


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
        self._stdout_lines: deque[str] = deque(maxlen=2000)
        self._stderr_lines: deque[str] = deque(maxlen=2000)
        self._log_threads: list[Thread] = []
        self._echo_logs = bool(os.getenv("HONUA_TEST_SERVER_LOGS"))

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
        connection_string = self._normalize_connection_string(self.connection_string)
        env.update({
            "ASPNETCORE_URLS": f"http://localhost:{self.port}",
            "ASPNETCORE_ENVIRONMENT": "Test",
            "ConnectionStrings__DefaultConnection": connection_string,
            "ConnectionStrings__honua": connection_string,
            # Keep the Python/GDAL harness anonymous so protocol clients can exercise the full surface.
            # As of #1144 the bypass requires an explicit operator acknowledgement.
            "HONUA_DEV_AUTH": "true",
            "HONUA_DEV_AUTH_ALLOW_BYPASS": "true",
            "HONUA_REGISTER_TEST_INFRASTRUCTURE": "true",
            "HONUA_SKIP_MIGRATIONS": "true",
            # Disable HTTPS redirection for tests
            "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "false",
        })
        # Keep protocol/client compatibility runs focused on external behavior.
        # Query-cache behavior is covered separately in dedicated .NET tests.
        env.setdefault(
            "Database__QueryCache__EnableAutomaticCaching",
            os.getenv("HONUA_TEST_ENABLE_QUERY_CACHE", "false"),
        )
        # Provide deterministic test keys for connection encryption unless supplied.
        env.setdefault(
            "Security__ConnectionEncryption__MasterKey",
            "test-master-key-that-is-at-least-32-characters-long-for-security",
        )
        env.setdefault(
            "Security__ConnectionEncryption__Salt",
            "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=",
        )
        env.setdefault(
            "Limits__Attachments__AllowedMimeTypes",
            "image/*,application/pdf,text/plain",
        )

        configuration = os.getenv("HONUA_TEST_CONFIGURATION", "Debug")

        # Start the server process
        self._process = subprocess.Popen(
            [
                "dotnet",
                "run",
                "--no-build",
                "--no-launch-profile",
                "--configuration",
                configuration,
                "--project",
                str(server_project),
            ],
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=str(self.project_root),
        )
        self._start_log_readers()

        # Wait for server to be healthy
        self._wait_for_health(timeout)

        return self

    def _wait_for_health(self, timeout: float):
        """Wait for the server to respond to health checks."""
        import httpx

        health_url = f"{self.base_url}/healthz/live"
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
                stdout = "".join(self._stdout_lines)
                stderr = "".join(self._stderr_lines)
                raise RuntimeError(
                    f"Server process died unexpectedly.\n"
                    f"stdout: {stdout}\n"
                    f"stderr: {stderr}"
                )

            time.sleep(0.5)

        raise RuntimeError(f"Server did not become healthy within {timeout}s")

    def _normalize_connection_string(self, value: str) -> str:
        """Normalize PostgreSQL connection strings for Npgsql."""
        if "://" not in value:
            return value

        parsed = urlparse(value)
        if not parsed.hostname:
            return value

        username = parsed.username or ""
        password = parsed.password or ""
        host = parsed.hostname or ""
        port = parsed.port or 5432
        database = parsed.path.lstrip("/") if parsed.path else ""

        parts = [
            f"Host={host}",
            f"Port={port}",
        ]
        if database:
            parts.append(f"Database={database}")
        if username:
            parts.append(f"Username={username}")
        if password:
            parts.append(f"Password={password}")

        return ";".join(parts)

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
            self._log_threads.clear()

    def _start_log_readers(self):
        """Drain server stdout/stderr to avoid pipe backpressure."""
        if not self._process:
            return

        def drain(stream, buffer):
            for line in iter(stream.readline, b""):
                text = line.decode(errors="ignore")
                buffer.append(text)
                if self._echo_logs:
                    sys.stdout.write(text)
            stream.close()

        if self._process.stdout:
            thread = Thread(target=drain, args=(self._process.stdout, self._stdout_lines), daemon=True)
            thread.start()
            self._log_threads.append(thread)

        if self._process.stderr:
            thread = Thread(target=drain, args=(self._process.stderr, self._stderr_lines), daemon=True)
            thread.start()
            self._log_threads.append(thread)

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
