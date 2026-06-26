from __future__ import annotations

import subprocess

import pytest

from honua_customcode_harness.sourcefetch import (
    SourceFetchError,
    clone_pinned,
)

GOOD_SHA = "b" * 40


class FakeGit:
    """Records git invocations and returns canned rev-parse output."""

    def __init__(self, head_sha: str, *, fail_on=None):
        self.head_sha = head_sha
        self.calls: list[list[str]] = []
        self._fail_on = fail_on or set()

    def __call__(self, cmd, cwd=None, capture_output=True, text=True, check=False):
        args = cmd[1:]  # drop leading "git"
        self.calls.append(args)
        verb = args[0]
        if verb in self._fail_on:
            return subprocess.CompletedProcess(cmd, 1, stdout="", stderr=f"{verb} boom")
        stdout = self.head_sha if verb == "rev-parse" else ""
        return subprocess.CompletedProcess(cmd, 0, stdout=stdout, stderr="")


def test_clone_pinned_verifies_head_matches(tmp_path) -> None:
    git = FakeGit(head_sha=GOOD_SHA)
    dest = clone_pinned("https://x/repo.git", GOOD_SHA, tmp_path / "src", runner=git)
    assert dest.exists()
    verbs = [c[0] for c in git.calls]
    assert "init" in verbs
    assert "fetch" in verbs
    assert "checkout" in verbs
    assert "rev-parse" in verbs


def test_clone_pinned_rejects_non_sha(tmp_path) -> None:
    git = FakeGit(head_sha=GOOD_SHA)
    with pytest.raises(SourceFetchError, match="non-SHA"):
        clone_pinned("https://x/repo.git", "main", tmp_path / "src", runner=git)
    # git must never be invoked for a bad ref.
    assert git.calls == []


def test_clone_pinned_fails_when_head_mismatches(tmp_path) -> None:
    # Remote resolved the SHA to a different commit -> hard fail.
    git = FakeGit(head_sha="c" * 40)
    with pytest.raises(SourceFetchError, match="verification failed"):
        clone_pinned("https://x/repo.git", GOOD_SHA, tmp_path / "src", runner=git)


def test_clone_pinned_falls_back_when_fetch_by_sha_unsupported(tmp_path) -> None:
    # First fetch (by sha) fails; fallback path still gets to checkout/verify.
    class FlakyGit(FakeGit):
        def __init__(self, head_sha):
            super().__init__(head_sha)
            self._sha_fetch_failed = False

        def __call__(self, cmd, **kw):
            args = cmd[1:]
            if args[0] == "fetch" and not self._sha_fetch_failed:
                self._sha_fetch_failed = True
                self.calls.append(args)
                return subprocess.CompletedProcess(cmd, 128, stdout="", stderr="no allowReachableSHA1")
            return super().__call__(cmd, **kw)

    git = FlakyGit(head_sha=GOOD_SHA)
    dest = clone_pinned("https://x/repo.git", GOOD_SHA, tmp_path / "src", runner=git)
    assert dest.exists()
    # Should have retried fetch after the sha-fetch failure.
    fetch_calls = [c for c in git.calls if c[0] == "fetch"]
    assert len(fetch_calls) >= 2
