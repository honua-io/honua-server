import copy
import hashlib
import importlib.util
import json

import pytest
from pathlib import Path


SCRIPT = Path(__file__).with_name("check-distributed-availability.py")
SPEC = importlib.util.spec_from_file_location("distributed_availability", SCRIPT)
assert SPEC and SPEC.loader
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)

REVISION = "a" * 40
DIGEST = "sha256:" + "b" * 64
REPLICAS = ("replica-a", "replica-b")
QUERY_SHA = hashlib.sha256(b"sum(failed_serving_outcomes) / sum(serving_requests)").hexdigest()


def _sha(payload: bytes | str) -> str:
    value = payload.encode() if isinstance(payload, str) else payload
    return hashlib.sha256(value).hexdigest()


def comparison(root: Path) -> dict:
    artifacts = []
    cells = []
    definitions = [
        ("unequal-load", (9000, 1000), 2, 1),
        ("reservoir-overflow", (5000, 4200), 3, 2),
        ("in-band-errors", (4000, 2000), 1, 9),
        ("rolling-replacement", (4500, 1500), 2, 3),
    ]
    for index, (cell_id, loads, http_failures, in_band_failures) in enumerate(definitions, start=1):
        denominator = sum(loads)
        numerator = http_failures + in_band_failures
        ledger_payload = json.dumps(
            {"cell": cell_id, "requests": denominator, "failures": numerator}, sort_keys=True
        ).encode()
        query_payload = json.dumps(
            {"cell": cell_id, "numerator": numerator, "denominator": denominator}, sort_keys=True
        ).encode()
        ledger_id = f"{cell_id}-ledger"
        query_id = f"{cell_id}-query"
        for artifact_id, payload, kind, population in (
            (ledger_id, ledger_payload, "request-ledger", denominator),
            (query_id, query_payload, "sli-query-result", 2),
        ):
            path = f"{artifact_id}.json"
            (root / path).write_bytes(payload)
            artifacts.append(
                {
                    "id": artifact_id,
                    "kind": kind,
                    "path": path,
                    "uri": f"https://github.com/honua-io/honua-server/actions/runs/777/artifacts/{index * 10 + len(artifacts)}",
                    "sha256": _sha(payload),
                    "observationCount": population,
                }
            )

        first_http = http_failures
        first_in_band = in_band_failures // 2
        ledger_rows = [
            {
                "logicalReplica": REPLICAS[0],
                "incarnation": "replica-a-v1",
                "requests": loads[0],
                "httpFailures": first_http,
                "inBandFailures": first_in_band,
            },
            {
                "logicalReplica": REPLICAS[1],
                "incarnation": "replica-b-v1",
                "requests": loads[1],
                "httpFailures": 0,
                "inBandFailures": in_band_failures - first_in_band,
            },
        ]
        if cell_id == "rolling-replacement":
            ledger_rows[0]["requests"] = 2500
            ledger_rows.insert(
                1,
                {
                    "logicalReplica": REPLICAS[0],
                    "incarnation": "replica-a-v2",
                    "requests": loads[0] - 2500,
                    "httpFailures": 0,
                    "inBandFailures": 0,
                },
            )
        workload = {
            "requestsByReplica": dict(zip(REPLICAS, loads, strict=True)),
            "requestsPerProtocol": {"FeatureServer": denominator},
            "injectedHttpFailures": http_failures,
            "injectedInBandFailures": in_band_failures,
            "rawArtifactIds": [ledger_id],
        }
        if cell_id == "rolling-replacement":
            workload["replacement"] = {
                "logicalReplica": "replica-a",
                "oldIncarnation": "replica-a-v1",
                "newIncarnation": "replica-a-v2",
                "startedAt": "2026-09-01T10:30:00Z",
                "readyAt": "2026-09-01T10:30:20Z",
                "completedAt": "2026-09-01T10:30:30Z",
                "imageDigest": DIGEST,
            }

        cells.append(
            {
                "id": cell_id,
                "candidateIdentity": {"serverRevision": REVISION, "imageDigest": DIGEST},
                "window": {"startedAt": "2026-09-01T10:00:00Z", "endedAt": "2026-09-01T11:00:00Z"},
                "workload": workload,
                "ledger": {
                    "rawArtifactIds": [ledger_id],
                    "rows": ledger_rows,
                    "numerator": numerator,
                    "denominator": denominator,
                    "sampleCount": denominator,
                },
                "queryResults": [
                    {
                        "queriedReplica": replica,
                        "numerator": numerator,
                        "denominator": denominator,
                        "value": numerator / denominator,
                        "rawArtifactIds": [query_id],
                    }
                    for replica in REPLICAS
                ],
            }
        )

    expression = "sum(failed_serving_outcomes) / sum(serving_requests)"
    result = {
        "schema": "honua.distributed-availability-comparison/v1",
        "status": "completed",
        "candidateIdentity": {"serverRevision": REVISION, "imageDigest": DIGEST},
        "topology": {
            "replicas": [
                {"id": REPLICAS[0], "failureDomain": "zone-a", "imageDigest": DIGEST},
                {"id": REPLICAS[1], "failureDomain": "zone-b", "imageDigest": DIGEST},
            ]
        },
        "query": {
            "language": "promql",
            "expression": expression,
            "version": "2026.1",
            "sha256": _sha(expression),
            "owner": "release-engineering",
            "alert": "https://github.com/honua-io/honua-server/blob/" + REVISION + "/docs/alerts/availability.md",
            "runbook": "https://github.com/honua-io/honua-server/blob/" + REVISION + "/docs/runbooks/availability.md",
            "tolerance": 0.0,
        },
        "rawArtifacts": artifacts,
        "cells": cells,
    }
    # Synthetic individual observations exercise the validator, not a live candidate.
    for cell in cells:
        rows = []
        for group in cell['ledger']['rows']:
            for i in range(group['requests']):
                rows.append(dict(id=f'{group["incarnation"]}-{i}', logicalReplica=group['logicalReplica'],
                                 incarnation=group['incarnation'], protocol='FeatureServer',
                                 at='2026-09-01T10:35:00Z' if group['incarnation'].endswith('v2') else '2026-09-01T10:15:00Z',
                                 httpStatus=500 if i < group['httpFailures'] else 200,
                                 inBandError=group['httpFailures'] <= i < group['httpFailures']+group['inBandFailures']))
        raw_ledger = dict(cell=cell['id'], candidateIdentity=cell['candidateIdentity'], window=cell['window'],
                          populationMode='all-serving-requests', samplingFailures=[], requests=rows,
                          replacement=cell['workload'].get('replacement'))
        raw_query = dict(candidateIdentity=cell['candidateIdentity'], window=cell['window'], query=result['query'],
                         results=[{k:v for k,v in r.items() if k != 'rawArtifactIds'} for r in cell['queryResults']])
        for artifact_id, document in ((cell['id']+'-ledger', raw_ledger), (cell['id']+'-query', raw_query)):
            artifact = next(a for a in artifacts if a['id'] == artifact_id)
            payload = json.dumps(document).encode()
            (root / artifact['path']).write_bytes(payload)
            artifact['sha256'] = _sha(payload)
    return result


def failures(value: dict, root: Path) -> list[str]:
    return gate.evaluate(value, REVISION, root, DIGEST, QUERY_SHA)


def test_exact_two_replica_four_cell_comparison_passes(tmp_path):
    assert failures(comparison(tmp_path), tmp_path) == []


def test_all_four_named_cells_are_mandatory(tmp_path):
    value = comparison(tmp_path)
    value["cells"].pop()
    assert any("4/4" in item for item in failures(value, tmp_path))


def test_unequal_load_cell_must_really_be_unequal(tmp_path):
    value = comparison(tmp_path)
    cell = value["cells"][0]
    cell["workload"]["requestsByReplica"] = {"replica-a": 5000, "replica-b": 5000}
    assert any("unequal-load" in item for item in failures(value, tmp_path))


def test_every_cell_must_exercise_both_replicas(tmp_path):
    value = comparison(tmp_path)
    cell = value["cells"][0]
    cell["ledger"]["rows"] = [cell["ledger"]["rows"][0]]
    cell["ledger"]["denominator"] = 9000
    cell["ledger"]["sampleCount"] = 9000
    cell["ledger"]["numerator"] = 2
    cell["workload"]["requestsByReplica"] = {"replica-a": 9000, "replica-b": 0}
    cell["workload"]["injectedInBandFailures"] = 0
    assert any("every comparison replica" in item for item in failures(value, tmp_path))


def test_overflow_cell_must_exceed_4096_per_protocol(tmp_path):
    value = comparison(tmp_path)
    value["cells"][1]["workload"]["requestsPerProtocol"]["FeatureServer"] = 4096
    assert any("reservoir-overflow" in item for item in failures(value, tmp_path))


def test_in_band_cell_must_exercise_2xx_protocol_errors(tmp_path):
    value = comparison(tmp_path)
    cell = value["cells"][2]
    cell["workload"]["injectedInBandFailures"] = 0
    for row in cell["ledger"]["rows"]:
        row["inBandFailures"] = 0
    assert any("in-band-errors" in item for item in failures(value, tmp_path))


def test_rolling_cell_requires_a_real_incarnation_change(tmp_path):
    value = comparison(tmp_path)
    replacement = value["cells"][3]["workload"]["replacement"]
    replacement["newIncarnation"] = replacement["oldIncarnation"]
    assert any("rolling-replacement" in item for item in failures(value, tmp_path))


def test_rolling_cell_requires_strictly_ordered_timeline(tmp_path):
    value = comparison(tmp_path)
    replacement = value["cells"][3]["workload"]["replacement"]
    replacement["readyAt"] = replacement["startedAt"]
    assert any("rolling-replacement" in item for item in failures(value, tmp_path))


def test_both_replicas_must_return_the_ledger_ratio(tmp_path):
    value = comparison(tmp_path)
    value["cells"][0]["queryResults"][1]["value"] = 0.5
    assert any("replica-b" in item and "ledger ratio" in item for item in failures(value, tmp_path))


def test_http_and_in_band_failures_both_enter_numerator(tmp_path):
    value = comparison(tmp_path)
    value["cells"][2]["ledger"]["numerator"] -= 1
    assert any("ledger numerator" in item for item in failures(value, tmp_path))


def test_every_cell_is_bound_to_the_immutable_candidate(tmp_path):
    value = comparison(tmp_path)
    value["cells"][1]["candidateIdentity"]["serverRevision"] = "c" * 40
    assert any("candidate identity" in item for item in failures(value, tmp_path))


def test_tampered_raw_population_fails(tmp_path):
    value = comparison(tmp_path)
    (tmp_path / "unequal-load-ledger.json").write_text("tampered", encoding="utf-8")
    assert any("hash mismatch" in item for item in failures(value, tmp_path))


@pytest.mark.parametrize('mutation', [
    lambda raw: raw['requests'].pop(),
    lambda raw: raw['requests'].append(copy.deepcopy(raw['requests'][0])),
    lambda raw: raw['requests'][10].update(inBandError=True),
    lambda raw: raw['requests'][10].update(at='2026-09-01T11:01:00Z'),
    lambda raw: raw.update(samplingFailures=['lost events']),
    lambda raw: raw.update(populationMode='retained-tail'),
])
def test_raw_ledger_tampering_with_recomputed_hash_fails(tmp_path, mutation):
    value = comparison(tmp_path)
    artifact = value['rawArtifacts'][0]
    path = tmp_path / artifact['path']
    raw = json.loads(path.read_text())
    mutation(raw)
    payload = json.dumps(raw).encode()
    path.write_bytes(payload)
    artifact['sha256'] = _sha(payload)
    assert any('raw source population' in item for item in failures(value, tmp_path))


def test_query_export_cannot_be_replaced_by_hashed_assertion(tmp_path):
    value = comparison(tmp_path)
    artifact = value['rawArtifacts'][1]
    payload = b'{"value":1.0}'
    (tmp_path / artifact['path']).write_bytes(payload)
    artifact['sha256'] = _sha(payload)
    assert any('raw source population' in item for item in failures(value, tmp_path))


def test_independently_pinned_image_is_required(tmp_path):
    value = comparison(tmp_path)
    assert any('independently pinned' in item for item in gate.evaluate(value, REVISION, tmp_path, 'sha256:'+'c'*64, QUERY_SHA))


def test_rehashed_query_cannot_revise_the_prefrozen_contract(tmp_path):
    value = comparison(tmp_path)
    value['query']['expression'] = 'return 1'
    value['query']['sha256'] = _sha('return 1')
    assert any('independently frozen' in item for item in failures(value, tmp_path))
