"""Defense-in-depth guards for the sanctioned custom-code Batch worker images.

Custom-code (untrusted, operator-supplied GP tool) execution is AWS-Batch-only
(ADR-0063). These images are the ONE sanctioned execution path, so their safety
guards must be asserted, not assumed. The token/credential scrub is locked in by
``test_sandbox.py``; this module locks in the container-level guard: both
custom-code worker Dockerfiles (python + dotnet) must declare a **non-root**
``USER`` and keep the harness as their ``ENTRYPOINT``, so a future edit that drops
back to root — which would run untrusted user code as root in the container —
trips a red test instead of silently landing.
"""

from __future__ import annotations

from pathlib import Path

import pytest

# tests/ -> harness/ -> worker-customcode-python/ -> docker/
_DOCKER_ROOT = Path(__file__).resolve().parents[3]

_PYTHON_DOCKERFILE = _DOCKER_ROOT / "worker-customcode-python" / "Dockerfile"
_DOTNET_DOCKERFILE = _DOCKER_ROOT / "worker-customcode-dotnet" / "Dockerfile"

_ALL_DOCKERFILES = (_PYTHON_DOCKERFILE, _DOTNET_DOCKERFILE)


def _last_user_directive(dockerfile: Path) -> str | None:
    """Return the argument of the final ``USER`` instruction, or None if absent."""
    last: str | None = None
    for raw in dockerfile.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if line.upper().startswith("USER "):
            last = line[len("USER ") :].strip()
    return last


@pytest.mark.parametrize("dockerfile", _ALL_DOCKERFILES, ids=lambda p: p.parent.name)
def test_dockerfile_exists(dockerfile: Path) -> None:
    assert dockerfile.is_file(), f"missing sanctioned worker Dockerfile: {dockerfile}"


@pytest.mark.parametrize("dockerfile", _ALL_DOCKERFILES, ids=lambda p: p.parent.name)
def test_dockerfile_declares_non_root_user(dockerfile: Path) -> None:
    user = _last_user_directive(dockerfile)
    assert user is not None, (
        f"{dockerfile.parent.name}/Dockerfile declares no USER directive; the "
        "custom-code worker must run as a non-root user (ADR-0063)."
    )

    # Reject root by name or uid (e.g. "root", "0", "0:0", "root:root").
    uid = user.split(":", 1)[0].strip()
    assert uid not in {"root", "0"}, (
        f"{dockerfile.parent.name}/Dockerfile runs as root ('USER {user}'); untrusted "
        "custom-code must run as a non-root user in the sanctioned Batch container "
        "(ADR-0063)."
    )
    # The images standardize on uid 1001 (matching docker/worker-gdal).
    assert uid == "1001", (
        f"{dockerfile.parent.name}/Dockerfile USER is '{user}', expected the non-root "
        "uid 1001 the custom-code workers standardize on."
    )


@pytest.mark.parametrize("dockerfile", _ALL_DOCKERFILES, ids=lambda p: p.parent.name)
def test_dockerfile_entrypoint_is_the_harness(dockerfile: Path) -> None:
    text = dockerfile.read_text(encoding="utf-8")
    assert "ENTRYPOINT" in text, f"{dockerfile.parent.name}/Dockerfile declares no ENTRYPOINT."
    # The sanctioned entrypoint is the credential-scrubbing harness, not a raw shell
    # or the user's own code.
    assert (
        "honua-customcode-harness" in text or "Honua.CustomCode.Harness" in text
    ), (
        f"{dockerfile.parent.name}/Dockerfile ENTRYPOINT is not the custom-code harness; "
        "the harness is what scrubs HONUA_JOB_TOKEN + ambient credentials before user code."
    )
