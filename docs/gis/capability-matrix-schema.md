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
  "schemaVersion": "1.0.0",
  "generator": "scripts/ci/generate-capability-matrix.py",
  "trackingIssue": "#2893",
  "sourceArtifacts": [
    "docs/gis/data/feature-catalog.json",
    "docs/cite-status.md",
    "docs/gis/data/geoservices-rest-parity.json",
    "docs/gis/data/capability-keys.v1.json",
    "docs/gis/data/capability-no-surface-allowlist.v1.json"
  ],
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
      "interop": [ { "clientLane": "js", "protocol": "wms" }, { "clientLane": "js-cesium", "protocol": "wms" } ],
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
| `interop` | array | `{ clientLane, protocol }` pairs from the client-interop crosswalk. |
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
