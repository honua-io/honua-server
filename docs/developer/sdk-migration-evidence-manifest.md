# SDK Migration Automation Evidence Manifest

This is the per-SDK submission contract for the migration automation surfaces
recorded by the `sdk-server-compatibility.yml` workflow. It is the target SDK
repositories implement against when wiring live migration smoke flows for
[honua-server#1018](https://github.com/honua-io/honua-server/issues/1018).

Server compatibility cells already declare migration automation surfaces under
`protocol_surfaces_by_sdk` and `migration_automation_by_sdk` (see the [SDK
Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md) evidence contract). Today
every per-SDK entry records `status: "unsupported"` with a `linked_ticket`
pointing at the SDK-owned migration toolkit issue. This manifest pins the
schema, status vocabulary, surface set, and the minimum fields each SDK lane
must POST/emit so the central matrix can flip a cell from `unsupported` to
`supported` or `failed` deterministically.

## Surface Set

The following migration automation surfaces are gated on this manifest. They
match the surface labels already emitted in
`migration_automation_by_sdk` cells.

| Surface label | What the SDK must drive | Server contract entry point |
|---|---|---|
| `migration-scan` | Run a source inventory scan and read the resulting scan artifact through typed SDK models. | `POST /api/v1/admin/import/scan` (source kinds: `arcgis-feature-service`, `geoserver`, `ogc-wfs`, `ogc-wms`, `ogc-wmts`). |
| `arcgis-import` | Start an ArcGIS GeoServices import job, poll its status, and read the resulting parity/readiness evidence. | `POST /api/v1/admin/import/geoservices/start`, `GET /api/v1/admin/import/jobs/{id}`. |
| `geoserver-dry-run` | Generate a GeoServer dry-run apply-plan artifact and validate the typed artifact model. | `POST /api/v1/admin/import/geoserver/start` with dry-run options. |
| `migration-evidence` | Retrieve the artifact chain (inventory, manifest, parity evidence, cutover readiness) and deserialize via SDK models. | Server migration artifact models exposed through `Honua.Sdk.Abstractions.Migration.*` equivalents per language. |

Surfaces are intentionally coarse. Adding a new surface requires updating this
manifest, the workflow's `protocol_surfaces_by_sdk` / `migration_automation_by_sdk`
defaults, and the cell evidence schema together.

## Status Vocabulary

Every per-surface entry in `migration_automation_by_sdk[sdk]` must use one of:

| Status | Meaning | `passed` value | When to use |
|---|---|---|---|
| `unsupported` | SDK does not yet implement the wrapper for this surface. | `false` | Default. SDK-owned ticket is open and the SDK has no smoke coverage yet. |
| `supported` | SDK implements the wrapper and the live smoke flow passed in this cell. | `true` | Smoke ran end-to-end against the seeded server and SDK assertions held. |
| `failed` | SDK implements the wrapper but the live smoke flow failed in this cell. | `false` | Smoke ran but the SDK call or assertion failed. Cell must include a failure-log tail. |
| `skipped` | SDK is intentionally not exercising the surface in this cell. | `false` | Cell is exercising a non-migration cross-section (for example a release-cell schema check) and skipping the migration smoke is recorded, not silent. |

`unsupported` and `failed` look similar in CI output but mean different things.
`unsupported` is "we did not try"; `failed` is "we tried and it broke". The
matrix decision logic must keep them distinct.

## Per-Cell Evidence Record (Minimum Fields)

Each `compat-result.json` cell already records the fields below for migration
automation. SDK lanes that want a cell flipped from `unsupported` must produce
the same shape with a `supported` or `failed` status and the additional
artifact fields.

```json
{
  "migration_automation": {
    "required": false,
    "status": "supported",
    "passed": true,
    "reason": "Live SDK migration smoke flow passed against seeded server."
  },
  "migration_automation_by_sdk": {
    "js": [
      {
        "surface": "migration-scan",
        "status": "supported",
        "passed": true,
        "linked_ticket": "honua-sdk-js#105",
        "artifact": {
          "kind": "scan",
          "source_kind": "arcgis-feature-service",
          "scan_id": "scan-2026-05-20-abc123",
          "artifact_path": "results/sdk-js/migration-scan.json",
          "duration_ms": 4821
        }
      },
      {
        "surface": "arcgis-import",
        "status": "supported",
        "passed": true,
        "linked_ticket": "honua-sdk-js#105",
        "artifact": {
          "kind": "import-job",
          "job_id": "job-2026-05-20-def456",
          "terminal_status": "succeeded",
          "artifact_path": "results/sdk-js/arcgis-import.json",
          "poll_count": 7,
          "duration_ms": 18204
        }
      },
      {
        "surface": "geoserver-dry-run",
        "status": "supported",
        "passed": true,
        "linked_ticket": "honua-sdk-js#105",
        "artifact": {
          "kind": "apply-plan",
          "plan_id": "plan-2026-05-20-ghi789",
          "artifact_path": "results/sdk-js/geoserver-dry-run.json",
          "operations_total": 4,
          "unsupported_operations": 0
        }
      },
      {
        "surface": "migration-evidence",
        "status": "supported",
        "passed": true,
        "linked_ticket": "honua-sdk-js#105",
        "artifact": {
          "kind": "evidence-bundle",
          "manifest_id": "manifest-2026-05-20-jkl012",
          "artifact_path": "results/sdk-js/migration-evidence.json",
          "parity_passed": true,
          "readiness": "ready"
        }
      }
    ]
  }
}
```

The `artifact.artifact_path` is relative to the cell evidence root and must be
uploaded alongside `compat-result.json`. The central summary collects these
paths and references them in the matrix run artifact.

## Sample Row: JS SDK ArcGIS Scan + Import (Reference Fixture)

The smallest non-trivial submission a JS SDK lane can target is the seeded
ArcGIS feature service scan followed by a queued import job. The expected cell
shape, given the current `HONUA_SDK_SEED_PROFILE`
(`tests/seed/base-schema.sql:test_service:layer0`), is:

```json
{
  "surface": "migration-scan",
  "status": "supported",
  "passed": true,
  "linked_ticket": "honua-sdk-js#105",
  "artifact": {
    "kind": "scan",
    "source_kind": "arcgis-feature-service",
    "scan_id": "<scan-id-from-server>",
    "artifact_path": "results/sdk-js/migration-scan.json",
    "duration_ms": 0
  }
}
```

A failing run for the same surface must downgrade `status` to `failed`,
`passed` to `false`, and add a `failure` block:

```json
{
  "surface": "migration-scan",
  "status": "failed",
  "passed": false,
  "linked_ticket": "honua-sdk-js#105",
  "failure": {
    "stage": "scan-start",
    "exit_code": 1,
    "log_tail_path": "results/sdk-js/migration-scan.failure.log"
  }
}
```

## Submission Rules

- SDK lanes must not change `surface` labels without an accompanying server-side
  schema update in `sdk-server-compatibility.yml` and this manifest.
- An `unsupported` entry must keep `linked_ticket` populated so reviewers can
  jump to the owning SDK issue from any cell.
- A `supported` entry must include an `artifact.artifact_path` that exists in
  the uploaded cell artifact. Empty `supported` claims are rejected by the
  central runner.
- A `failed` entry must include a `failure.log_tail_path` with at least the
  last 80 lines of the smoke log. The cell remains supported in the matrix
  shape but the per-surface status surfaces in release evidence.
- The order of entries within `migration_automation_by_sdk[sdk]` must follow
  the surface set order in the table above so diffs stay readable.

## Release Evidence Promotion

A migration surface counts as release evidence for a given SDK only when:

1. The latest scheduled or release-gated `sdk-server-compatibility.yml` run
   records `status: "supported"` and `passed: true` for that surface in every
   supported cell for the SDK.
2. The corresponding SDK package version in `sdk_package_versions` matches a
   published release, not a `trunk` placeholder.
3. The release-train manifest links the exact `sdk-compatibility-matrix-<run-id>`
   artifact that contains those cells.

Until all three conditions hold, the surface stays at `unsupported` in the
matrix and the doc copy in
[Compatibility and Automated Migration Evidence](../contributor/compatibility-and-migration-evidence.md)
must continue to call the SDK-driven migration row a backlog gap under
[honua-server#1018](https://github.com/honua-io/honua-server/issues/1018).

## Related References

- [SDK Compatibility Matrix](SDK_COMPATIBILITY_MATRIX.md) - matrix policy and
  evidence contract.
- [SDK Standards Coverage by Language](SDK_STANDARDS_COVERAGE.md) - per-language
  surface positioning, including the migration automation row.
- [Compatibility and Automated Migration Evidence](../contributor/compatibility-and-migration-evidence.md) -
  external claim index that consumes the matrix.
- Closed SDK migration toolkit tickets:
  [honua-sdk-js#105](https://github.com/honua-io/honua-sdk-js/issues/105),
  [honua-sdk-python#49](https://github.com/honua-io/honua-sdk-python/issues/49),
  [honua-sdk-dotnet#134](https://github.com/honua-io/honua-sdk-dotnet/issues/134).
