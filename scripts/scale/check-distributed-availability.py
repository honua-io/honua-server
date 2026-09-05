#!/usr/bin/env python3
"""Validate the exact two-replica, four-cell platform availability comparison."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


SCHEMA = "honua.distributed-availability-comparison/v1"
REQUIRED_CELLS = (
    "unequal-load",
    "reservoir-overflow",
    "in-band-errors",
    "rolling-replacement",
)
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
HASH_PATTERN = re.compile(r"^[0-9a-f]{64}$")
ARTIFACT_PATH_PATTERN = re.compile(
    r"^/honua-io/honua-server/actions/runs/[1-9][0-9]*/artifacts/[1-9][0-9]*$"
)


def _mapping(value: object) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _finite(value: object) -> bool:
    return not isinstance(value, bool) and isinstance(value, (int, float)) and math.isfinite(value)


def _count(value: object, *, allow_zero: bool = False) -> bool:
    return (
        isinstance(value, int)
        and not isinstance(value, bool)
        and (value >= 0 if allow_zero else value > 0)
    )


def _nonempty(value: object) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _time(value: object) -> datetime | None:
    if not isinstance(value, str):
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        return None
    return parsed.astimezone(timezone.utc)


def _artifact_url(value: object) -> bool:
    if not _nonempty(value):
        return False
    parsed = urlparse(str(value))
    return (
        parsed.scheme == "https"
        and parsed.netloc == "github.com"
        and bool(ARTIFACT_PATH_PATTERN.fullmatch(parsed.path))
    )


def _refs(value: object, known: set[str]) -> bool:
    return (
        isinstance(value, list)
        and bool(value)
        and all(isinstance(item, str) and item in known for item in value)
        and len(value) == len(set(value))
    )


def _raw_artifacts(
    receipt: dict[str, Any], root: Path, failures: list[str]
) -> tuple[set[str], dict[str, dict[str, Any]]]:
    value = receipt.get("rawArtifacts")
    if not isinstance(value, list) or not value:
        failures.append("raw observation artifacts are missing")
        return set(), {}

    resolved_root = root.resolve()
    known: set[str] = set()
    indexed: dict[str, dict[str, Any]] = {}
    for entry in value:
        artifact = _mapping(entry)
        artifact_id = artifact.get("id")
        label = str(artifact_id) if _nonempty(artifact_id) else "<missing>"
        if not _nonempty(artifact_id) or artifact_id in known:
            failures.append(f"{label}: raw artifact id is missing or duplicated")
            continue
        artifact_id = str(artifact_id)
        known.add(artifact_id)
        indexed[artifact_id] = artifact
        if not _nonempty(artifact.get("kind")):
            failures.append(f"{label}: raw artifact kind is missing")
        if not _artifact_url(artifact.get("uri")):
            failures.append(f"{label}: raw artifact URI is not immutable")
        if not HASH_PATTERN.fullmatch(str(artifact.get("sha256", ""))):
            failures.append(f"{label}: raw artifact SHA-256 is invalid")
        if not _count(artifact.get("observationCount")):
            failures.append(f"{label}: raw observation population is missing")
        relative = artifact.get("path")
        if not _nonempty(relative):
            failures.append(f"{label}: raw artifact path is missing")
            continue
        path = (resolved_root / str(relative)).resolve()
        if path.parent != resolved_root or not path.is_file():
            failures.append(f"{label}: raw artifact is absent from the evidence bundle")
            continue
        if hashlib.sha256(path.read_bytes()).hexdigest() != artifact.get("sha256"):
            failures.append(f"{label}: raw artifact hash mismatch")
    return known, indexed


def _query(value: object, failures: list[str]) -> float | None:
    query = _mapping(value)
    expression = query.get("expression")
    for field in ("language", "expression", "version", "owner", "alert", "runbook"):
        if not _nonempty(query.get(field)):
            failures.append(f"platform query {field} is missing")
    if _nonempty(expression) and query.get("sha256") != hashlib.sha256(
        str(expression).encode()
    ).hexdigest():
        failures.append("platform query hash does not bind the frozen expression")
    tolerance = query.get("tolerance")
    if not _finite(tolerance) or tolerance < 0 or tolerance > 1e-9:
        failures.append("platform query tolerance must be explicitly frozen between 0 and 1e-9")
        return None
    return float(tolerance)


def _topology(
    value: object, candidate: dict[str, Any], failures: list[str]
) -> tuple[str, str] | tuple[()]:
    topology = _mapping(value)
    replicas = topology.get("replicas")
    if not isinstance(replicas, list) or len(replicas) != 2:
        failures.append("comparison topology must contain exactly two replicas")
        return ()
    ids: list[str] = []
    domains: set[str] = set()
    for replica in replicas:
        item = _mapping(replica)
        if not _nonempty(item.get("id")):
            failures.append("topology replica id is missing")
            continue
        ids.append(str(item["id"]))
        if not _nonempty(item.get("failureDomain")):
            failures.append(f"{item['id']}: failure domain is missing")
        else:
            domains.add(str(item["failureDomain"]))
        if item.get("imageDigest") != candidate.get("imageDigest"):
            failures.append(f"{item['id']}: image digest differs from the candidate")
    if len(set(ids)) != 2:
        failures.append("topology replica ids must be unique")
    if len(domains) != 2:
        failures.append("the two replicas must occupy distinct failure domains")
    return tuple(ids) if len(ids) == 2 else ()


def _ledger(
    cell_id: str,
    value: object,
    replicas: tuple[str, str],
    artifacts: set[str],
    artifact_index: dict[str, dict[str, Any]],
    failures: list[str],
) -> tuple[int, int, dict[str, int], set[str]] | None:
    ledger = _mapping(value)
    rows = ledger.get("rows")
    if not isinstance(rows, list) or not rows:
        failures.append(f"{cell_id}: authoritative request ledger rows are missing")
        return None
    if not _refs(ledger.get("rawArtifactIds"), artifacts):
        failures.append(f"{cell_id}: ledger raw artifact references are missing")

    requests_by_replica = {replica: 0 for replica in replicas}
    incarnations: set[str] = set()
    numerator = 0
    denominator = 0
    for row_value in rows:
        row = _mapping(row_value)
        replica = row.get("logicalReplica")
        incarnation = row.get("incarnation")
        if replica not in requests_by_replica or not _nonempty(incarnation):
            failures.append(f"{cell_id}: ledger row has an unknown replica or missing incarnation")
            continue
        requests = row.get("requests")
        http_failures = row.get("httpFailures")
        in_band_failures = row.get("inBandFailures")
        if not _count(requests) or not _count(http_failures, allow_zero=True) or not _count(
            in_band_failures, allow_zero=True
        ):
            failures.append(f"{cell_id}: ledger row counts are invalid")
            continue
        if http_failures + in_band_failures > requests:
            failures.append(f"{cell_id}: ledger failures exceed requests")
        requests_by_replica[str(replica)] += requests
        denominator += requests
        numerator += http_failures + in_band_failures
        incarnations.add(str(incarnation))

    if ledger.get("denominator") != denominator or ledger.get("sampleCount") != denominator:
        failures.append(f"{cell_id}: ledger denominator/sample population does not equal all requests")
    if ledger.get("numerator") != numerator:
        failures.append(f"{cell_id}: ledger numerator omits HTTP or in-band failed outcomes")
    if any(count <= 0 for count in requests_by_replica.values()):
        failures.append(f"{cell_id}: every comparison replica must receive request traffic")
    for artifact_id in ledger.get("rawArtifactIds", []):
        artifact = artifact_index.get(str(artifact_id), {})
        if artifact.get("kind") == "request-ledger" and artifact.get("observationCount") != denominator:
            failures.append(f"{cell_id}: request-ledger artifact population differs from the denominator")
    return numerator, denominator, requests_by_replica, incarnations


def _cell(
    cell: dict[str, Any],
    candidate: dict[str, Any],
    replicas: tuple[str, str],
    artifacts: set[str],
    artifact_index: dict[str, dict[str, Any]],
    tolerance: float,
    failures: list[str],
) -> None:
    cell_id = str(cell.get("id", "<missing>"))
    if cell.get("candidateIdentity") != candidate:
        failures.append(f"{cell_id}: candidate identity differs from the comparison")
    window = _mapping(cell.get("window"))
    started, ended = _time(window.get("startedAt")), _time(window.get("endedAt"))
    if started is None or ended is None or ended <= started:
        failures.append(f"{cell_id}: exact UTC observation window is missing or invalid")

    workload = _mapping(cell.get("workload"))
    if not _refs(workload.get("rawArtifactIds"), artifacts):
        failures.append(f"{cell_id}: workload raw artifact references are missing")
    ledger_result = _ledger(
        cell_id, cell.get("ledger"), replicas, artifacts, artifact_index, failures
    )
    if ledger_result is None:
        return
    numerator, denominator, observed_loads, incarnations = ledger_result

    declared_loads = workload.get("requestsByReplica")
    if declared_loads != observed_loads:
        failures.append(f"{cell_id}: workload replica split differs from the request ledger")
    http_injected = workload.get("injectedHttpFailures")
    in_band_injected = workload.get("injectedInBandFailures")
    rows = _mapping(cell.get("ledger")).get("rows", [])
    observed_http = sum(_mapping(row).get("httpFailures", 0) for row in rows)
    observed_in_band = sum(_mapping(row).get("inBandFailures", 0) for row in rows)
    if http_injected != observed_http or in_band_injected != observed_in_band:
        failures.append(f"{cell_id}: injected error workload differs from the request ledger")

    if cell_id == "unequal-load" and len(set(observed_loads.values())) != 2:
        failures.append("unequal-load: the two replicas received equal traffic")
    if cell_id == "reservoir-overflow":
        per_protocol = workload.get("requestsPerProtocol")
        if not isinstance(per_protocol, dict) or not per_protocol or any(
            not _count(count) or count <= 4096 for count in per_protocol.values()
        ):
            failures.append("reservoir-overflow: every exercised protocol must exceed 4,096 requests")
    if cell_id == "in-band-errors" and (not _count(observed_in_band) or observed_in_band <= 0):
        failures.append("in-band-errors: no HTTP-2xx in-band failures were exercised")
    if cell_id == "rolling-replacement":
        replacement = _mapping(workload.get("replacement"))
        old_id = replacement.get("oldIncarnation")
        new_id = replacement.get("newIncarnation")
        timeline = [
            _time(replacement.get("startedAt")),
            _time(replacement.get("readyAt")),
            _time(replacement.get("completedAt")),
        ]
        if (
            replacement.get("logicalReplica") not in replicas
            or not _nonempty(old_id)
            or not _nonempty(new_id)
            or old_id == new_id
            or old_id not in incarnations
            or new_id not in incarnations
            or replacement.get("imageDigest") != candidate.get("imageDigest")
            or any(item is None for item in timeline)
            or not timeline[0] < timeline[1] < timeline[2]
            or started is None
            or ended is None
            or not started <= timeline[0] <= timeline[2] <= ended
        ):
            failures.append("rolling-replacement: old/new incarnations and ordered in-window timeline are required")

    query_results = cell.get("queryResults")
    if not isinstance(query_results, list) or len(query_results) != 2:
        failures.append(f"{cell_id}: exactly two replica query results are required")
        return
    seen: set[str] = set()
    ledger_ratio = numerator / denominator if denominator else math.nan
    for result_value in query_results:
        result = _mapping(result_value)
        replica = result.get("queriedReplica")
        if replica not in replicas or replica in seen:
            failures.append(f"{cell_id}: query results must cover each replica exactly once")
            continue
        seen.add(str(replica))
        if not _refs(result.get("rawArtifactIds"), artifacts):
            failures.append(f"{cell_id}/{replica}: query raw artifact references are missing")
        if result.get("numerator") != numerator or result.get("denominator") != denominator:
            failures.append(f"{cell_id}/{replica}: query numerator/denominator differs from the ledger")
        value = result.get("value")
        if not _finite(value) or not math.isclose(
            float(value), ledger_ratio, rel_tol=0, abs_tol=tolerance
        ):
            failures.append(f"{cell_id}/{replica}: query value differs from the ledger ratio")


def _validate_source_population(receipt, root, failures):
    """Recompute each cell from the retained individual request ledger and query export."""
    try:
        artifacts = {a['id']: a for a in receipt['rawArtifacts']}
        def source(ids, kind):
            matches = [artifacts[i] for i in ids if artifacts[i]['kind'] == kind]
            if len(matches) != 1:
                raise ValueError(f'exactly one {kind} source artifact is required')
            artifact = matches[0]
            path = (root.resolve() / artifact['path']).resolve()
            if path.parent != root.resolve():
                raise ValueError('source path escapes evidence bundle')
            payload = path.read_bytes()
            if hashlib.sha256(payload).hexdigest() != artifact['sha256']:
                raise ValueError('source hash mismatch')
            return json.loads(payload)
        for cell in receipt['cells']:
            ledger = source(cell['ledger']['rawArtifactIds'], 'request-ledger')
            for field in ('candidateIdentity', 'window'):
                if ledger[field] != cell[field]:
                    raise ValueError(f'{cell["id"]}: source {field} differs')
            if ledger['cell'] != cell['id'] or ledger['populationMode'] != 'all-serving-requests':
                raise ValueError('source cell or request population mode differs')
            if ledger['samplingFailures'] != []:
                raise ValueError('request collection failed')
            rows = ledger['requests']
            counts, protocols, per_replica_protocol, ids = {}, {}, {}, set()
            start, end = (_time(cell['window'][k]) for k in ('startedAt', 'endedAt'))
            for request in rows:
                request_id = request['id']
                if not isinstance(request_id, str) or not request_id or request_id in ids:
                    raise ValueError('duplicate or missing request identity')
                ids.add(request_id)
                at = _time(request['at'])
                if at is None or start is None or end is None or not start <= at < end:
                    raise ValueError('request outside exact window')
                protocol = request['protocol']
                if not isinstance(protocol, str) or not protocol:
                    raise ValueError('missing protocol')
                status = request['httpStatus']
                in_band = request['inBandError']
                if isinstance(status, bool) or not isinstance(status, int) or not 100 <= status <= 599 or not isinstance(in_band, bool):
                    raise ValueError('invalid HTTP/in-band outcome')
                if in_band and not 200 <= status < 300:
                    raise ValueError('in-band failures must be HTTP 2xx; failures cannot be double counted')
                key = (request['logicalReplica'], request['incarnation'])
                totals = counts.setdefault(key, [0, 0, 0])
                totals[0] += 1
                totals[1] += int(status >= 500)
                totals[2] += int(in_band)
                protocols[protocol] = protocols.get(protocol, 0) + 1
                per_key = (request['logicalReplica'], protocol)
                per_replica_protocol[per_key] = per_replica_protocol.get(per_key, 0) + 1
            observed = [{'logicalReplica': replica, 'incarnation': incarnation, 'requests': totals[0],
                         'httpFailures': totals[1], 'inBandFailures': totals[2]}
                        for (replica, incarnation), totals in sorted(counts.items())]
            declared = sorted(cell['ledger']['rows'], key=lambda row: (row['logicalReplica'], row['incarnation']))
            if observed != declared or len(rows) != cell['ledger']['denominator']:
                raise ValueError(f'{cell["id"]}: ledger totals differ from individual source requests')
            if protocols != cell['workload']['requestsPerProtocol']:
                raise ValueError('declared protocol population differs from source requests')
            if cell['id'] == 'reservoir-overflow' and any(count <= 4096 for count in per_replica_protocol.values()):
                raise ValueError('reservoir-overflow: every replica/protocol must overflow its own 4096-slot ring')
            if cell['id'] == 'in-band-errors' and not any(t[1] for t in counts.values()):
                raise ValueError('in-band-errors: HTTP 5xx failures must also be exercised')
            if cell['id'] == 'rolling-replacement':
                replacement = cell['workload']['replacement']
                if ledger['replacement'] != replacement:
                    raise ValueError('rolling replacement differs from source event timeline')
                for request in rows:
                    if request['logicalReplica'] != replacement['logicalReplica']:
                        continue
                    at = _time(request['at'])
                    if request['incarnation'] == replacement['oldIncarnation'] and at >= _time(replacement['completedAt']):
                        raise ValueError('old incarnation serves after replacement completes')
                    if request['incarnation'] == replacement['newIncarnation'] and at < _time(replacement['readyAt']):
                        raise ValueError('new incarnation serves before readiness')
            for result in cell['queryResults']:
                query = source(result['rawArtifactIds'], 'sli-query-result')
                if any(query[k] != cell[k] for k in ('candidateIdentity', 'window')) or query['query'] != receipt['query']:
                    raise ValueError('query export candidate/window/frozen query differs')
                if query['results'] != [{k: v for k, v in r.items() if k != 'rawArtifactIds'} for r in cell['queryResults']]:
                    raise ValueError('replica query values differ from retained query export')
    except (KeyError, TypeError, ValueError, OSError) as exc:
        failures.append(f'raw source population: {exc}')


def evaluate(receipt: dict[str, Any], expected_revision: str, artifact_root: Path, expected_image_digest: str) -> list[str]:
    """Return every comparison-contract failure; an empty list is the only pass."""
    failures: list[str] = []
    if receipt.get("schema") != SCHEMA:
        failures.append(f"comparison schema must be {SCHEMA}")
    if receipt.get("status") != "completed":
        failures.append("comparison status must be completed")
    if not SHA_PATTERN.fullmatch(expected_revision):
        failures.append("expected exact candidate revision is invalid")
    candidate = _mapping(receipt.get("candidateIdentity"))
    if candidate.get("serverRevision") != expected_revision:
        failures.append("comparison candidate revision differs from the expected exact candidate")
    if not DIGEST_PATTERN.fullmatch(str(candidate.get("imageDigest", ""))):
        failures.append("comparison candidate image digest is missing; source builds are inadmissible")

    if not DIGEST_PATTERN.fullmatch(expected_image_digest) or candidate.get("imageDigest") != expected_image_digest:
        failures.append("candidate image differs from independently pinned image digest")
    replicas = _topology(receipt.get("topology"), candidate, failures)
    tolerance = _query(receipt.get("query"), failures)
    artifacts, artifact_index = _raw_artifacts(receipt, artifact_root, failures)
    cells = receipt.get("cells")
    if not isinstance(cells, list):
        cells = []
    by_id = {
        str(_mapping(cell).get("id")): _mapping(cell)
        for cell in cells
        if _nonempty(_mapping(cell).get("id"))
    }
    if len(cells) != 4 or set(by_id) != set(REQUIRED_CELLS):
        failures.append("distributed comparison must contain the exact 4/4 mandatory cells")
    if len(by_id) != len(cells):
        failures.append("distributed comparison cell ids must be unique and non-empty")

    if len(replicas) == 2 and tolerance is not None:
        for cell_id in REQUIRED_CELLS:
            if cell_id in by_id:
                _cell(
                    by_id[cell_id],
                    candidate,
                    replicas,
                    artifacts,
                    artifact_index,
                    tolerance,
                    failures,
                )
    _validate_source_population(receipt, artifact_root, failures)
    return failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--artifact-root", type=Path, required=True)
    parser.add_argument("--expected-revision", required=True)
    parser.add_argument("--expected-image-digest", required=True)
    args = parser.parse_args()
    try:
        receipt = json.loads(args.receipt.read_text(encoding="utf-8"))
        failures = evaluate(receipt, args.expected_revision, args.artifact_root, args.expected_image_digest)
    except (OSError, ValueError, TypeError, KeyError, AttributeError) as exc:
        failures = [str(exc)]
    if failures:
        print("distributed-availability: FAIL")
        for failure in failures:
            print(f"- {failure}")
        return 1
    print("distributed-availability: PASS â€” exact two replicas and 4/4 ledger-equal cells")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
