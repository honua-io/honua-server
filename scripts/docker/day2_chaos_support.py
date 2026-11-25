"""Shared models, probes, GeoServices parsers, and JWT helpers for Day-2 chaos checks."""

from __future__ import annotations

import base64
import hashlib
import json
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Protocol

REPLICA_CONVERGENCE_BUDGET_SECONDS = 5.0
BOUNDED_DISK_FULL_STATUSES = frozenset({500, 503, 507})
PG_DB = "honua_dev"
PG_USER = "honua_user"


def utc_now_iso() -> str:
    return datetime.now(UTC).isoformat().replace("+00:00", "Z")


@dataclass(frozen=True)
class CommandSpec:
    args: tuple[str, ...]
    timeout_seconds: float = 60.0
    stdin_text: str | None = None
    env: dict[str, str] | None = None


@dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: str = ""
    stderr: str = ""


@dataclass(frozen=True)
class HttpRequest:
    method: str
    url: str
    headers: dict[str, str] = field(default_factory=dict)
    body: bytes | None = None
    timeout_seconds: float = 5.0


@dataclass(frozen=True)
class HttpResponse:
    status: int
    body: str = ""
    headers: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class ScenarioConfig:
    image: str = "honua-server:local"
    compose_file: str = "docker-compose.yml"
    base_url: str = "http://127.0.0.1:8080"
    replica_url: str = "http://127.0.0.1:8082"
    jwks_url: str = "http://127.0.0.1:8089"
    rotated_token: str = ""
    mttr_budget_seconds: float = 30.0
    poll_interval_seconds: float = 0.5
    max_wait_seconds: float = 60.0


@dataclass(frozen=True)
class ProbeSpec:
    url: str
    expected_status: int = 200
    max_wait_seconds: float | None = None
    headers: dict[str, str] = field(default_factory=dict)
    predicate: Callable[[HttpResponse], bool] | None = None


@dataclass(frozen=True)
class ProbeResult:
    reached: bool
    elapsed_seconds: float
    last_status: int
    last_body: str


@dataclass(frozen=True)
class OutcomeSpec:
    name: str
    category: str
    mttr_seconds: float
    budget_seconds: float
    data_loss: bool
    checks: dict[str, bool]
    details: str


@dataclass(frozen=True)
class ScenarioOutcome:
    name: str
    category: str
    passed: bool
    mttr_seconds: float
    mttr_budget_seconds: float
    within_mttr_budget: bool
    data_loss: bool
    details: str
    checks: dict[str, bool] = field(default_factory=dict)


class CommandAdapter(Protocol):
    def run(self, spec: CommandSpec) -> CommandResult: ...


class HttpAdapter(Protocol):
    def send(self, request: HttpRequest) -> HttpResponse: ...


@dataclass(frozen=True)
class ClockAdapter:
    now: Callable[[], float] = time.monotonic
    sleep: Callable[[float], None] = time.sleep
    utc_iso: Callable[[], str] = utc_now_iso


@dataclass(frozen=True)
class ChaosEnvironment:
    config: ScenarioConfig
    command: CommandAdapter
    http: HttpAdapter
    clock: ClockAdapter = field(default_factory=ClockAdapter)


def compose_run(
    env: ChaosEnvironment, subargs: tuple[str, ...], stdin_text: str | None = None,
) -> CommandResult:
    cmd = ("docker", "compose", "-f", env.config.compose_file, *subargs)
    return env.command.run(
        CommandSpec(cmd, stdin_text=stdin_text, env={"HONUA_SERVER_IMAGE": env.config.image}),
    )


def verify_running_image(env: ChaosEnvironment) -> bool:
    res = compose_run(env, ("ps", "--format", "{{.Image}}", "honua"))
    if res.returncode != 0:
        return False
    images = [line.strip() for line in res.stdout.splitlines() if line.strip()]
    return bool(images) and all(image == env.config.image for image in images)


def psql_exec(env: ChaosEnvironment, sql: str) -> CommandResult:
    return compose_run(env, ("exec", "-T", "postgres", "psql", "-U", PG_USER, "-d", PG_DB, "-Atc", sql))


def http_get(env: ChaosEnvironment, url: str) -> HttpResponse:
    return env.http.send(HttpRequest("GET", url))


def http_post(env: ChaosEnvironment, url: str) -> HttpResponse:
    return env.http.send(HttpRequest("POST", url))


def _load_json_dict(body: str) -> dict[str, object]:
    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        return {}
    return payload if isinstance(payload, dict) else {}


def parse_json_int(body: str, key: str, default: int = -1) -> int:
    value = _load_json_dict(body).get(key)
    return value if isinstance(value, int) and not isinstance(value, bool) else default


def parse_json_str(body: str, key: str) -> str:
    value = _load_json_dict(body).get(key)
    return value if isinstance(value, str) else ""


def parse_services_count(body: str) -> int:
    services = _load_json_dict(body).get("services")
    return len(services) if isinstance(services, list) else -1


def parse_feature_count(body: str) -> int:
    payload = _load_json_dict(body)
    features = payload.get("features")
    if isinstance(features, list):
        return len(features)
    count = payload.get("count")
    return count if isinstance(count, int) and not isinstance(count, bool) else -1


def _b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def build_probe_jwt(kid: str, issuer: str, now_epoch: int = 1_900_000_000) -> str:
    header = _b64url(json.dumps({"alg": "RS256", "typ": "JWT", "kid": kid}, separators=(",", ":")).encode())
    claims = {
        "iss": issuer,
        "sub": "day2-chaos-probe",
        "aud": "honua-server",
        "iat": now_epoch,
        "exp": now_epoch + 300,
    }
    payload = _b64url(json.dumps(claims, separators=(",", ":")).encode())
    signing_input = f"{header}.{payload}".encode("ascii")
    signature = _b64url(hashlib.sha256(signing_input).digest())
    return f"{header}.{payload}.{signature}"


def wait_for_probe(env: ChaosEnvironment, spec: ProbeSpec) -> ProbeResult:
    started = env.clock.now()
    deadline = started + (spec.max_wait_seconds or env.config.max_wait_seconds)
    while True:
        last = env.http.send(HttpRequest("GET", spec.url, headers=spec.headers))
        matched = last.status == spec.expected_status and (
            spec.predicate is None or spec.predicate(last)
        )
        if matched or env.clock.now() >= deadline:
            return ProbeResult(matched, round(env.clock.now() - started, 3), last.status, last.body)
        env.clock.sleep(env.config.poll_interval_seconds)


def evaluate_outcome(spec: OutcomeSpec) -> ScenarioOutcome:
    within_budget = spec.mttr_seconds <= spec.budget_seconds
    passed = all(spec.checks.values()) and not spec.data_loss and within_budget
    return ScenarioOutcome(
        spec.name, spec.category, passed, round(spec.mttr_seconds, 3),
        round(spec.budget_seconds, 3), within_budget, spec.data_loss,
        spec.details, dict(spec.checks),
    )
