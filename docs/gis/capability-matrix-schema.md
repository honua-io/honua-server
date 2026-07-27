# Capability Matrix Schema (`capability-matrix.v1.json`)

Tracking issue: [#2893](https://github.com/honua-io/honua-server/issues/2893).

`docs/gis/data/capability-matrix.v1.json` is the Phase-A evidence aggregation
described in the issue #2893 design amendment: it joins
[`feature-catalog.json`](data/feature-catalog.json) (test counts per
capability), [`cite-status.md`](../cite-status.md) (OGC CITE pass rates),
[`geoservices-rest-parity.json`](data/geoservices-rest-parity.json) (Esri
parity status), and [`capability-keys.v1.json`](data/capability-keys.v1.json)
(the crosswalk references) into one per-capability record — "which
capabilities does the customer use → what evidence exists" without a manual
five-way join.

**This is explicitly a temporary home.** Per the design amendment on #2893,
this aggregation lifts-and-shifts to the public `honua-evidence` repo in Phase
B (#2892). The schema below is the contract that survives the move — moving
the aggregation is a CI-job relocation, not a redesign.

**"Implemented" here is evidence-count-derived, not a GA sign-off.** The
`maturity` field below counts `feature-catalog.json` entries by their
generator-resolved tier; it does not by itself certify that a capability meets
the bar for being advertised GA. See
[`capability-keys-schema.md#ga-criteria-2946`](capability-keys-schema.md#ga-criteria-2946)
for that bar and
[`capability-ga-regrade-2026-07.md`](capability-ga-regrade-2026-07.md) for the
2026-07 re-grade decisions applied against it.

## Generation

```bash
python3 scripts/ci/generate-capability-matrix.py           # regenerate
python3 scripts/ci/generate-capability-matrix.py --check   # drift-check only (CI)
```

Dependency-light by design (Python 3 standard library only — `json`, `re`,
`argparse`; no external packages), matching the `scripts/ci/*.py` /
`scripts/client-compat/*.py` convention already used in this repo. It is run
by the `.github/workflows/capability-matrix-aggregation.yml` workflow on
`trunk` pushes that touch a source artifact, on a weekly schedule, and via
`workflow_dispatch`; the regenerated file is uploaded as a workflow artifact
and the job fails if the freshly-generated output differs from the committed
file (drift gate), printing the regeneration command above.

## Document shape

```json
{
  "schemaVersion": "1.1.0",
  "generator": "scripts/ci/generate-capability-matrix.py",
  "trackingIssue": "#2893",
  "sourceArtifacts": [
    "docs/gis/data/feature-catalog.json",
    "docs/cite-status.md",
    "docs/gis/data/geoservices-rest-parity.json",
    "docs/gis/data/capability-keys.v1.json",
    "docs/gis/data/capability-no-surface-allowlist.v1.json",
    "tests/baselines/client-compat/"
  ],
  "evidenceFreshness": {
    "anchor": "2026-07-09T00:00:00Z",
    "maxAgeDays": 14,
    "basis": "Newest run_date across the committed client-compat envelopes; deterministic by design so the drift gate stays byte-stable (see #2897)."
  },
  "capabilities": [
    {
      "key": "serve.wms",
      "displayName": "WMS 1.3",
      "category": "Serve",
      "edition": "Community",
      "entryCount": 1,
      "provingTestCount": 3,
      "maturity": { "implemented": 1 },
      "noSurface": null,
      "cite": [
        { "suite": "WMS 1.3", "profile": "default", "passed": 199, "total": 199, "passRate": 100.0 }
      ],
      "parity": [],
      "esriAssess": [],
      "interop": [
        { "clientLane": "js", "protocol": "wms", "freshness": { "state": "stale", "ageDays": 62, "runDate": "2026-05-07T14:05:53.364Z" } },
        { "clientLane": "js-cesium", "protocol": "wms", "freshness": { "state": "unknown", "ageDays": null, "runDate": null } }
      ],
      "geobench": [ "wms-getmap", "wms-reprojection", "wms-getfeatureinfo", "wms-filtered" ]
    }
  ]
}
```

### `capabilities[]` fields

| Field | Type | Description |
|---|---|---|
| `key`, `displayName`, `category`, `edition` | string | Copied from `capability-keys.v1.json`. |
| `entryCount` | number | Count of `feature-catalog.json` entries stamped with this capability. |
| `provingTestCount` | number | Sum of `proving_tests` across those entries. |
| `maturity` | object | Count per maturity tier (`implemented`, `experimental`) among this capability's entries. |
| `noSurface` | object \| null | The matching row from `capability-no-surface-allowlist.v1.json` when `entryCount` is `0`; otherwise `null`. |
| `cite` | array | Zero or one OGC CITE suite result (`{ suite, profile, passed, total, passRate }`) when this capability's protocol has an official ETS (see `CAPABILITY_TO_CITE_SUITE` in the generator script). Empty for capabilities with no CITE suite. |
| `parity` | array | Esri GeoServices REST parity rows (`{ serviceId, displayName, parity }`) from `geoservices-rest-parity.json`, joined via the `esriCompatMatrix` crosswalk. |
| `esriAssess` | array of string | `honua-esri-assess` registry keys that crosswalk to this capability. |
| `interop` | array | `{ clientLane, protocol, freshness }` pairs from the client-interop crosswalk. `freshness` is the per-pair evidence-freshness stamp (issue #2897 item 4): `state` is `fresh`, `stale`, or `unknown`; `ageDays` is the whole-day distance between this pair's committed envelope `run_date` and the top-level `evidenceFreshness.anchor`; `runDate` echoes the envelope's authoritative observation timestamp. `unknown` means no committed envelope (or no usable timestamp) exists for the pair; `stale` means the envelope is not green under the strict all-pass/terminal criteria shared with `scripts/ci/capability-impact.py`, or its observation is more than `maxAgeDays` behind the anchor, or (defensively) it claims an observation newer than the anchor — negative `ageDays` fails closed as `stale` with an explicit `reason` field. Future-dated `run_date` values never get this far: the generator rejects them outright as corrupt input. |
| `geobench` | array of string | geobench scenario names that benchmark this capability. |

## Consuming this artifact

Downstream repos should treat this file as read-only evidence, joined against
[`capability-keys.v1.json`](capability-keys-schema.md) for the authoritative
key list:

- **honua-samples** (`#1`) — join a sample's `sample.json` capability keys
  against this file to show "which of this sample's capabilities have CITE
  coverage / parity evidence / geobench numbers" on the samples gallery.
- **SDK coverage snapshots** (honua-sdk-js/-dotnet/-python) — cross-reference
  `sdk-coverage.v1.json`'s per-capability covered/partial/none status with this
  file's `entryCount`/`provingTestCount` to flag SDK claims with no server-side
  evidence.
- **honua-evidence** (`#1`) — ingests this file directly today (Phase A) and
  will ingest the relocated, identically-shaped artifact once the aggregation
  moves in Phase B.

### Top-level `evidenceFreshness`

| Field | Type | Description |
|---|---|---|
| `anchor` | string \| null | The newest `run_date` across all committed `tests/baselines/client-compat/**/*.cert.json` envelopes (`null` when no envelope has a usable timestamp). A future-dated `run_date` is corrupt input: the generator refuses to run (hard error) rather than let it anchor the window as self-reported fresh or make the committed bytes wall-clock dependent. All `interop[].freshness.ageDays` values are measured against this anchor. |
| `maxAgeDays` | number | Staleness threshold (default `14`): a pair whose evidence is more than this many days behind the anchor is `stale`. |
| `basis` | string | Human-readable statement of the anchor rule. |

The anchor is deliberately **not** wall-clock time: this artifact is
byte-for-byte drift-gated (the aggregation workflow and the merge train both
re-run the bare generator and require an identical result), so any field
derived from "now" would go stale every day with no source change. Relative
staleness against the newest committed evidence still surfaces the intended
signal — a lane that has not produced a green envelope while others have moved
on shows as `stale` — but if *all* lanes stop refreshing simultaneously, the
matrix alone will not flag it; the per-PR capability-impact comparison report
(`scripts/ci/capability-impact.py select`) computes wall-clock freshness for
that case. Consumers that need wall-clock staleness can compute it directly
from `runDate`.

**Schema versioning:** `schemaVersion` follows semver — additive optional
fields bump the minor version (the `freshness`/`evidenceFreshness` addition is
`1.0.0` → `1.1.0`); removals or meaning changes to existing fields require a
major bump and coordination with the `honua-evidence` ingest (Phase A
consumer).

## Drift and freshness

Unlike `feature-catalog.json` (drift-gated by an xUnit `[Fact]` in
`Honua.Architecture.Tests`), `capability-matrix.v1.json` itself is
drift-checked by the CI workflow (`--check` mode above), not a C# test.
Treat a workflow failure exactly like a failed architecture test: regenerate
locally and commit the result.

`cite-status.md`'s per-suite table — one of this artifact's source inputs —
*is* independently parsed by a C# architecture test,
`CiteStatusComplianceDriftTests`
(`tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/CiteStatusComplianceDriftTests.cs`,
#2924). That test uses the same row-parsing pattern as
`generate-capability-matrix.py`'s `parse_cite_status`, but for a different
join: it gates the `x-honua-cite-compliance` vendor extension in
`src/Honua.Server/openapi.json` and the other four `*-openapi.json` files
against `cite-status.md`'s totals, independently of this capability-matrix
aggregation.
