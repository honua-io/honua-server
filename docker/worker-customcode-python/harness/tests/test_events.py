"""The honua.gp.events/v1 journal: ordering, caps, truncation, malformed lines."""
from __future__ import annotations

import io
import json
from concurrent.futures import ThreadPoolExecutor

import pytest

from honua_customcode_harness.context import _StdLogger
from honua_customcode_harness.events import SCHEMA_VERSION, EventJournal, attempt_from_env, read_events


def lines(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def test_records_carry_version_sequence_time_and_attempt(tmp_path):
    journal = EventJournal(tmp_path / "events.jsonl", attempt="7")
    journal.phase("setup")
    journal.report(42.5, "transform")
    journal.info("hello")
    journal.warn("careful")
    journal.artifact("out.txt", 12, sha256="ab" * 32)
    journal.failure("ValueError", "bad", phase="execute")
    journal.terminal("failed", 1, phase="execute")
    records = lines(tmp_path / "events.jsonl")
    assert [r["kind"] for r in records] == ["phase", "progress", "log", "log", "artifact", "failure", "terminal"]
    assert [r["seq"] for r in records] == list(range(7))
    assert all(r["v"] == SCHEMA_VERSION and r["attempt"] == "7" and r["at"].endswith("Z") for r in records)
    assert all(records[i]["elapsedMs"] <= records[i + 1]["elapsedMs"] for i in range(6))
    assert records[1]["percent"] == 42.5 and records[1]["phase"] == "transform"
    assert records[2]["level"] == "info" and records[3]["level"] == "warning"
    assert records[4]["artifact"] == {"name": "out.txt", "sizeBytes": 12, "sha256": "ab" * 32}
    assert records[5]["error"] == {"type": "ValueError", "message": "bad"}
    assert (records[6]["status"], records[6]["exitCode"], records[6]["phase"]) == ("failed", 1, "execute")


def test_event_cap_discloses_dropped_count_and_keeps_terminal(tmp_path):
    journal = EventJournal(tmp_path / "events.jsonl", max_events=5)
    for i in range(9):
        journal.info(str(i))
    journal.terminal("succeeded", 0)
    records = lines(tmp_path / "events.jsonl")
    assert len(records) == 7
    assert [r["kind"] for r in records[:5]] == ["log"] * 5
    assert records[5]["kind"] == "truncated" and records[5]["dropped"] == 4 and records[5]["maxEvents"] == 5
    assert records[6]["kind"] == "terminal" and records[6]["seq"] == 6
    assert journal.truncated and journal.dropped == 4


def test_byte_cap_counts_oversized_records_as_dropped(tmp_path):
    journal = EventJournal(tmp_path / "events.jsonl", max_bytes=400)
    for _ in range(50):
        journal.info("x" * 100)
    journal.terminal("succeeded", 0)
    records = lines(tmp_path / "events.jsonl")
    assert records[-1]["kind"] == "terminal"
    assert records[-2]["kind"] == "truncated" and records[-2]["dropped"] == 50 - (len(records) - 2)
    assert 1 <= len(records) - 2 < 50


def test_malformed_and_foreign_lines_read_as_text(tmp_path):
    path = tmp_path / "events.jsonl"
    journal = EventJournal(path)
    journal.phase("setup")
    with path.open("a", encoding="utf-8") as stream:
        stream.write("not json at all\n")
        stream.write('{"v": 99, "kind": "phase"}\n')
        stream.write('{"kind": "progress"}\n')
        stream.write("\n")
    journal.terminal("succeeded", 0)
    kinds = [(r["kind"], r.get("line")) for r in read_events(path)]
    assert kinds == [("phase", None), ("text", "not json at all"), ("text", '{"v": 99, "kind": "phase"}'),
                     ("text", '{"kind": "progress"}'), ("terminal", None)]


def test_concurrent_emitters_keep_sequence_contiguous(tmp_path):
    journal = EventJournal(tmp_path / "events.jsonl", max_events=300)
    with ThreadPoolExecutor(max_workers=16) as pool:
        list(pool.map(lambda i: (journal.report(50, "working"), journal.info(str(i))), range(500)))
    journal.terminal("succeeded", 0)
    records = lines(tmp_path / "events.jsonl")
    assert [r["seq"] for r in records] == list(range(len(records)))
    assert len(records) == 302 and records[-2]["dropped"] == 700


def test_mirror_keeps_the_stderr_text_unchanged(tmp_path):
    stderr = io.StringIO()
    journal = EventJournal(tmp_path / "events.jsonl", mirror=_StdLogger(stderr))
    journal.report(50, "transform")
    journal.info("hello")
    journal.warn("careful")
    assert stderr.getvalue() == "[tool] INFO  progress  50.0% — transform\n[tool] INFO  hello\n[tool] WARN  careful\n"


def test_non_finite_progress_is_rejected_and_unknown_kind_refused(tmp_path):
    journal = EventJournal(tmp_path / "events.jsonl")
    with pytest.raises(ValueError):
        journal.report(float("nan"), "x")
    with pytest.raises(ValueError):
        journal.emit("bogus")


def test_attempt_from_env_prefers_explicit_then_batch():
    assert attempt_from_env({"HONUA_ATTEMPT_ID": "a1", "AWS_BATCH_JOB_ATTEMPT": "3"}) == "a1"
    assert attempt_from_env({"AWS_BATCH_JOB_ATTEMPT": "3"}) == "3"
    assert attempt_from_env({}) == "1"
    assert attempt_from_env(None) == "1"
