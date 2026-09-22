"""Record one governed bounded-roster cell as a client executes it.

A lane script opens a :class:`Cell` per governed ``test_id``, drives the real
client through each scenario facet the governed row requires, and writes one
observation file. The receipt emitter (``emit_receipts.py``) later joins that
observation to the on-wire requests the recording proxy captured in the cell's
execution window and to the exact candidate, and builds the governed receipt.

Verdict rule (fail closed):

* every facet in the row's ``scenario_facets`` must have at least one check;
* every check must pass;
* otherwise the cell is ``fail`` and its notes name each failed or missing facet.

A cell is never ``skip``: a client that cannot exercise a governed facet is a
failing cell with the reason recorded, not an absent one.
"""
from __future__ import annotations

import json
import os
import re
import time
import traceback
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path

OBSERVATION_SCHEMA = "honua.bounded-roster-observation/v1"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def load_requirements() -> list[dict]:
    path = Path(os.environ.get("ROSTER_REQUIREMENTS", "/roster/requirements.json"))
    return json.loads(path.read_text(encoding="utf-8"))["requirements"]


def requirement_for(test_id: str) -> dict:
    rows = [row for row in load_requirements() if test_id in (row.get("test_ids") or ())]
    if len(rows) != 1:
        raise LookupError(f"{test_id!r} resolves to {len(rows)} governed rows, expected exactly one")
    return rows[0]


class CheckFailed(AssertionError):
    """A scenario check observed behaviour that does not satisfy the facet."""


def expect(condition: object, message: str) -> None:
    if not condition:
        raise CheckFailed(message)


class _Check:
    def __init__(self, facet: str, name: str) -> None:
        self.facet = facet
        self.name = name
        self.detail = ""
        self.request_url: str | None = None


class Cell:
    """One governed cell: the requirement row plus the checks the client ran."""

    def __init__(self, test_id: str, *, client_version_detail: str, protocol_version: str,
                 protocol_profile: str) -> None:
        self.requirement = requirement_for(test_id)
        self.test_id = test_id
        self.client_version_detail = client_version_detail
        self.protocol_version = protocol_version
        self.protocol_profile = protocol_profile
        self.primary_request_url: str | None = None
        self.checks: list[dict] = []
        self.started_at = utc_now()
        self._monotonic = time.monotonic()

    @contextmanager
    def check(self, facet: str, name: str):
        """Run one scenario check; an exception fails the check, never the lane."""
        if facet not in self.requirement["scenario_facets"]:
            raise ValueError(
                f"{self.test_id}: facet {facet!r} is not governed for this row "
                f"({self.requirement['scenario_facets']})")
        record = _Check(facet, name)
        started = time.monotonic()
        try:
            yield record
        except Exception as error:  # noqa: BLE001 - any client failure is evidence
            outcome = "fail"
            detail = f"{type(error).__name__}: {error}"
            if not isinstance(error, CheckFailed):
                detail += "\n" + "".join(traceback.format_exception_only(type(error), error)).strip()
            record.detail = (record.detail + " | " if record.detail else "") + detail
        else:
            outcome = "pass"
        self.checks.append({
            "facet": facet,
            "name": name,
            "outcome": outcome,
            "detail": record.detail,
            "request_url": record.request_url,
            "duration_ms": round((time.monotonic() - started) * 1000),
        })

    def verdict(self) -> tuple[str, list[str], str]:
        governed = list(self.requirement["scenario_facets"])
        exercised = [facet for facet in governed if any(c["facet"] == facet for c in self.checks)]
        missing = [facet for facet in governed if facet not in exercised]
        failed = [f"{c['facet']}:{c['name']}" for c in self.checks if c["outcome"] != "pass"]
        if missing or failed:
            parts = []
            if failed:
                parts.append("failed checks " + ", ".join(failed))
            if missing:
                parts.append("governed facets not exercised " + ", ".join(missing))
            return "fail", exercised, "; ".join(parts)
        return "pass", exercised, f"all {len(self.checks)} checks passed across {len(governed)} governed facets"

    def write(self) -> Path:
        status, exercised, summary = self.verdict()
        directory = Path(os.environ.get("ROSTER_OBSERVATIONS", "/run/observations"))
        directory.mkdir(parents=True, exist_ok=True)
        name = re.sub(r"[^A-Za-z0-9._-]+", "_", self.test_id) + ".json"
        observation = {
            "schema": OBSERVATION_SCHEMA,
            "test_case_id": self.test_id,
            "canonical_client": self.requirement["canonical_client"],
            "client_lane": self.requirement["client_lane"],
            "client_version": self.requirement["client_version"],
            "client_version_detail": self.client_version_detail,
            "surface": self.requirement["surface"],
            "operation": self.requirement["operation"],
            "protocol_version": self.protocol_version,
            "protocol_profile": self.protocol_profile,
            "started_at": self.started_at,
            "finished_at": utc_now(),
            "duration_ms": round((time.monotonic() - self._monotonic) * 1000),
            "status": status,
            "exercised_capabilities": exercised,
            "summary": summary,
            "primary_request_url": self.primary_request_url,
            "checks": self.checks,
        }
        path = directory / name
        path.write_text(json.dumps(observation, indent=2) + "\n", encoding="utf-8")
        print(f"[{status}] {self.test_id}: {summary}", flush=True)
        return path
