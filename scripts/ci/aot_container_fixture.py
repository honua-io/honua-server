"""Shared ephemeral PostGIS and Native AOT container fixture for CI."""

from __future__ import annotations

import socket
import subprocess
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class DockerSession:
    """Ephemeral Docker network and containers booted for --image mode."""

    network: str
    pg_container: str
    app_container: str
    base_url: str


@dataclass(frozen=True)
class ContainerOptions:
    """Extra app settings and an optional memory cap with swap disabled."""

    environment: tuple[str, ...] = ()
    memory_limit_bytes: int | None = None


def _allocate_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


REPO_ROOT = Path(__file__).resolve().parents[2]
SEED_SQL_PATHS = (
    REPO_ROOT / "docker" / "cng" / "seed.sql",
    REPO_ROOT / "scripts" / "ci" / "seed-serving-formats.sql",
)


def _run_docker_checked(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=True)


def _run_docker_unchecked(args: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=False)


def _apply_seed_sql(pg_container: str, sql_path: Path) -> None:
    sql_text = sql_path.read_text(encoding="utf-8")
    subprocess.run(
        ["docker", "exec", "-i", pg_container, "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "honua_cng"],
        input=sql_text,
        capture_output=True,
        text=True,
        check=True,
    )


def _seed_cng_fixtures(session: DockerSession, boot_timeout: float) -> None:
    _run_docker_checked(["stop", session.app_container])
    for sql_path in SEED_SQL_PATHS:
        _apply_seed_sql(session.pg_container, sql_path)
    _run_docker_checked(["start", session.app_container])
    _wait_for_ready(session.base_url, boot_timeout)


def start_container_stack(
    image: str,
    postgis_image: str,
    boot_timeout: float,
    *,
    options: ContainerOptions = ContainerOptions(),
) -> DockerSession:
    """Boot PostGIS and the target NativeAOT image, seed CNG fixtures, and wait ready."""
    port = _allocate_port()
    tag = f"honua-aot-{int(time.time())}-{port}"
    session = DockerSession(f"{tag}-net", f"{tag}-pg", f"{tag}-app", f"http://127.0.0.1:{port}")
    _run_docker_checked(["network", "create", session.network])
    try:
        _start_postgis_container(session.network, session.pg_container, postgis_image)
        _wait_for_postgis(session.pg_container, boot_timeout)
        _start_app_container(session, image, port, options)
        _wait_for_ready(session.base_url, boot_timeout)
        _seed_cng_fixtures(session, boot_timeout)
        return session
    except Exception:
        stop_container_stack(session)
        raise


def _start_postgis_container(network: str, name: str, image: str) -> None:
    _run_docker_checked([
        "run", "-d", "--name", name, "--network", network,
        "-e", "POSTGRES_DB=honua_cng", "-e", "POSTGRES_USER=postgres",
        "-e", "POSTGRES_PASSWORD=cng_password", image,
    ])


def _start_app_container(
    session: DockerSession, image: str, port: int, options: ContainerOptions
) -> None:
    conn = (
        f"Server={session.pg_container};Port=5432;"
        "Database=honua_cng;User Id=postgres;Password=cng_password;"
    )
    memory = options.memory_limit_bytes
    memory_args = [f"--memory={memory}", f"--memory-swap={memory}"] if memory else []
    environment_args = [arg for entry in options.environment for arg in ("-e", entry)]
    _run_docker_checked([
        "run", "-d", "--name", session.app_container, "--network", session.network,
        "-p", f"127.0.0.1:{port}:8080", *memory_args, "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true",
        "--tmpfs", "/tmp/honua:rw,noexec,nosuid,size=256m,uid=1001,gid=1001,mode=750",
        "-e", "ASPNETCORE_ENVIRONMENT=Test",
        "-e", "HONUA_REGISTER_TEST_INFRASTRUCTURE=true",
        "-e", "Experimental__Features__RedshiftProvider=false",
        "-e", "Experimental__Features__SnowflakeProvider=false",
        "-e", "Experimental__Features__DatabricksProvider=false",
        "-e", "DataSource__Provider=postgres",
        "-e", f"ConnectionStrings__DefaultConnection={conn}",
        "-e", f"ConnectionStrings__honua={conn}",
        *environment_args, image,
    ])


def _wait_for_postgis(pg_name: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        proc = _run_docker_unchecked(["exec", pg_name, "pg_isready", "-U", "postgres"])
        if proc.returncode == 0:
            return
        time.sleep(1.0)
    raise RuntimeError(f"PostGIS container {pg_name} did not become ready within {timeout}s")


def _is_endpoint_ready(url: str) -> bool:
    try:
        with urllib.request.urlopen(url, timeout=3.0) as resp:
            return resp.status == 200
    except (urllib.error.URLError, TimeoutError, OSError):
        return False


def _wait_for_ready(base_url: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    url = base_url.rstrip("/") + "/healthz/ready"
    while time.monotonic() < deadline:
        if _is_endpoint_ready(url):
            return
        time.sleep(1.0)
    raise RuntimeError(f"NativeAOT server at {url} did not become ready within {timeout}s")


def stop_container_stack(session: DockerSession) -> str:
    """Capture app container logs and tear down ephemeral containers and network."""
    logs = read_container_logs(session.app_container)
    for name in (session.app_container, session.pg_container):
        _run_docker_unchecked(["rm", "-f", name])
    _run_docker_unchecked(["network", "rm", session.network])
    return logs


def read_container_logs(container_name: str) -> str:
    result = _run_docker_unchecked(["logs", container_name])
    return (result.stdout or "") + "\n" + (result.stderr or "")


def container_was_oom_killed(container_name: str) -> bool:
    result = _run_docker_checked(["inspect", "--format", "{{.State.OOMKilled}}", container_name])
    return {"true": True, "false": False}[result.stdout.strip()]
