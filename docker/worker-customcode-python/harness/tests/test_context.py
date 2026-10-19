from __future__ import annotations

import pytest

from honua_customcode_harness.context import (
    GpResult,
    GpStatus,
    OutputSink,
    OutputSizeExceeded,
)


def test_gpresult_succeeded_and_failed() -> None:
    ok = GpResult.succeeded("done")
    assert ok.ok is True
    assert ok.status == GpStatus.SUCCEEDED
    assert ok.message == "done"

    bad = GpResult.failed("nope")
    assert bad.ok is False
    assert bad.status == GpStatus.FAILED
    assert bad.message == "nope"


@pytest.mark.parametrize("name", ["sub\\file.txt", "C:relative.txt", "../out.txt"])
def test_output_sink_rejects_cross_platform_paths(tmp_path, name) -> None:
    file = tmp_path / "output.txt"
    file.write_text("data")
    with pytest.raises(ValueError):
        OutputSink(max_total_bytes=100).add_artifact(name, file)


def test_output_sink_rejects_duplicate_names_across_platforms(tmp_path) -> None:
    file = tmp_path / "output.txt"
    file.write_text("data")
    sink = OutputSink(max_total_bytes=100)
    sink.add_artifact("output.txt", file)
    with pytest.raises(ValueError, match="already registered"):
        sink.add_artifact("OUTPUT.txt", file)


def test_gpresult_failed_requires_message() -> None:
    with pytest.raises(ValueError):
        GpResult.failed("")


def test_output_sink_collects_artifacts(tmp_path) -> None:
    f = tmp_path / "out.txt"
    f.write_text("hello", encoding="utf-8")
    sink = OutputSink(max_total_bytes=1000)
    art = sink.add_artifact("out.txt", f)
    assert art.name == "out.txt"
    assert art.size_bytes == 5
    assert sink.total_bytes == 5
    assert len(sink.artifacts) == 1


def test_output_sink_rejects_missing_file(tmp_path) -> None:
    sink = OutputSink(max_total_bytes=1000)
    with pytest.raises(FileNotFoundError):
        sink.add_artifact("missing.txt", tmp_path / "missing.txt")


def test_output_sink_rejects_path_like_name(tmp_path) -> None:
    f = tmp_path / "x"
    f.write_text("x", encoding="utf-8")
    sink = OutputSink(max_total_bytes=1000)
    with pytest.raises(ValueError):
        sink.add_artifact("sub/dir.txt", f)


def test_output_sink_enforces_size_cap(tmp_path) -> None:
    big = tmp_path / "big.bin"
    big.write_bytes(b"0" * 100)
    sink = OutputSink(max_total_bytes=50)
    with pytest.raises(OutputSizeExceeded):
        sink.add_artifact("big.bin", big)
    # Nothing got staged.
    assert sink.artifacts == ()
    assert sink.total_bytes == 0


def test_output_sink_cumulative_cap(tmp_path) -> None:
    a = tmp_path / "a"
    a.write_bytes(b"0" * 30)
    b = tmp_path / "b"
    b.write_bytes(b"0" * 30)
    sink = OutputSink(max_total_bytes=50)
    sink.add_artifact("a", a)  # 30 ok
    with pytest.raises(OutputSizeExceeded):
        sink.add_artifact("b", b)  # 30 + 30 > 50
