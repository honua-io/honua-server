"""Clone + pin user code at an exact git SHA, then verify the checkout.

The harness only ever runs code at a fully-pinned 40-hex SHA. After fetching we
``git rev-parse HEAD`` and assert it equals the requested SHA — so a malicious
or misconfigured remote that resolves a ref to a different commit fails hard
instead of silently running unexpected code.
"""

from __future__ import annotations

import subprocess
from collections.abc import Sequence
from pathlib import Path

from .jobspec import is_valid_git_sha


class SourceFetchError(RuntimeError):
    """Raised when cloning/checkout/verification of user code fails."""


def _run_git(args: Sequence[str], *, cwd: Path | None = None, runner=subprocess.run) -> str:
    """Run a git command, returning stdout. Raises SourceFetchError on failure.

    ``runner`` is injectable so tests can stub git without a real binary.
    """
    cmd = ["git", *args]
    proc = runner(
        cmd,
        cwd=str(cwd) if cwd else None,
        capture_output=True,
        text=True,
        check=False,
    )
    if proc.returncode != 0:
        raise SourceFetchError(
            f"git {' '.join(args)} failed ({proc.returncode}): {proc.stderr.strip()}"
        )
    return (proc.stdout or "").strip()


def clone_pinned(
    repo_url: str,
    git_ref: str,
    dest: Path,
    *,
    runner=subprocess.run,
) -> Path:
    """Clone ``repo_url`` and check out exactly ``git_ref`` (a 40-hex SHA).

    Strategy: shallow ``init`` + ``fetch --depth 1 <sha>`` + ``checkout <sha>``.
    A direct fetch of the SHA keeps the download minimal and works whether or not
    the host allows fetch-by-sha (we fall back to a shallow clone if it does
    not). Finally assert ``rev-parse HEAD == sha``.
    """
    if not is_valid_git_sha(git_ref):
        # Defense-in-depth: never pass an unvalidated ref to git.
        raise SourceFetchError(
            f"refusing to fetch non-SHA git_ref {git_ref!r} (must be 40-hex)."
        )

    dest.mkdir(parents=True, exist_ok=True)
    _run_git(["init", "--quiet"], cwd=dest, runner=runner)
    _run_git(["remote", "add", "origin", repo_url], cwd=dest, runner=runner)

    try:
        _run_git(
            ["fetch", "--depth", "1", "origin", git_ref],
            cwd=dest,
            runner=runner,
        )
    except SourceFetchError:
        # Some hosts disable uploadpack.allowReachableSHA1InWant; fall back to a
        # shallow clone of the default branch + an unshallow-by-SHA fetch.
        _run_git(["fetch", "--depth", "1", "origin"], cwd=dest, runner=runner)
        _run_git(["fetch", "--depth", "50", "origin"], cwd=dest, runner=runner)

    _run_git(["checkout", "--quiet", "--detach", git_ref], cwd=dest, runner=runner)

    head = _run_git(["rev-parse", "HEAD"], cwd=dest, runner=runner)
    if head != git_ref:
        raise SourceFetchError(
            f"checkout verification failed: HEAD is {head!r} but expected {git_ref!r}."
        )
    return dest
