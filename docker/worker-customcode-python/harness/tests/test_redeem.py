"""Single-use code exchange for the job capability."""

from __future__ import annotations

import io
from urllib.error import HTTPError, URLError

import pytest

from honua_customcode_harness import redeem as redeem_module
from honua_customcode_harness.redeem import CODE_HEADER, RedemptionError, RedemptionTarget, redeem_capability

CODE = "c" * 64
CAPABILITY = "a" * 64
BASE = "https://api.honua.test"
TARGET = RedemptionTarget(BASE, "job-1", "1")


@pytest.fixture(autouse=True)
def _no_pause(monkeypatch) -> None:
    monkeypatch.setattr(redeem_module.time, "sleep", lambda _: None)


class FakeResponse(io.BytesIO):
    def __init__(self, body: bytes, status: int = 200) -> None:
        super().__init__(body)
        self.status = status


class ScriptedOpener:
    def __init__(self, *outcomes) -> None:
        self.outcomes = list(outcomes)
        self.requests = []

    def __call__(self, request, timeout):
        self.requests.append(request)
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


def _http_error(code: int) -> HTTPError:
    return HTTPError("https://api.honua.test", code, "status", {}, io.BytesIO(b""))


def test_redeem_posts_code_header_to_attempt_route_and_returns_capability() -> None:
    opener = ScriptedOpener(FakeResponse(CAPABILITY.encode()))

    capability = redeem_capability(RedemptionTarget(BASE + "/", "job-1", "2"), CODE, opener)

    assert capability == CAPABILITY
    request = opener.requests[0]
    assert request.get_method() == "POST"
    assert request.full_url == BASE + "/api/geoprocessing/jobs/job-1/attempts/2/capability"
    assert request.get_header(CODE_HEADER.capitalize()) == CODE
    assert CODE not in request.full_url


def test_redeem_retries_not_yet_dispatched_and_transport_errors() -> None:
    opener = ScriptedOpener(_http_error(409), URLError("down"), FakeResponse(CAPABILITY.encode()))

    assert redeem_capability(TARGET, CODE, opener) == CAPABILITY
    assert len(opener.requests) == 3


@pytest.mark.parametrize("status", [401, 410, 400])
def test_redeem_spent_expired_or_refused_code_fails_without_retry(status: int) -> None:
    opener = ScriptedOpener(_http_error(status))

    with pytest.raises(RedemptionError, match=str(status)):
        redeem_capability(TARGET, CODE, opener)
    assert len(opener.requests) == 1


def test_redeem_gives_up_after_retry_window(monkeypatch) -> None:
    clock = iter([0.0, 1.0, redeem_module.RETRY_WINDOW_SECONDS + 1.0])
    monkeypatch.setattr(redeem_module.time, "monotonic", lambda: next(clock))
    opener = ScriptedOpener(_http_error(503), _http_error(503))

    with pytest.raises(RedemptionError, match="503"):
        redeem_capability(TARGET, CODE, opener)


@pytest.mark.parametrize(
    ("target", "code"),
    [
        (RedemptionTarget("http://api.honua.test", "job-1", "1"), CODE),
        (RedemptionTarget(BASE, "../job", "1"), CODE),
        (RedemptionTarget(BASE, "job-1", "0"), CODE),
        (TARGET, "C" * 64),
        (TARGET, "short"),
    ],
)
def test_redeem_rejects_unsafe_inputs_before_any_request(target, code) -> None:
    opener = ScriptedOpener()

    with pytest.raises(RedemptionError):
        redeem_capability(target, code, opener)
    assert opener.requests == []


def test_redeem_rejects_malformed_capability_body() -> None:
    opener = ScriptedOpener(FakeResponse(b"<html>proxy</html>"))

    with pytest.raises(RedemptionError, match="malformed"):
        redeem_capability(TARGET, CODE, opener)
