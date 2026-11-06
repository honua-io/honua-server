# Production GA hardening audit

Status: **partial — not a GA clearance**.

Tracking: https://github.com/honua-io/honua-server/issues/6985

Baseline: `800c0ec9277733433f90cd56e6e53f35bda1b11b` (`origin/trunk`, fetched 2026-11-05). The shared checkout contains unrelated uncommitted work and is excluded. Three Sol review lanes cover security, design, and reliability; findings receive primary review and independent fix review.

## Findings under remediation

| Finding | Severity | Evidence and disposition |
| --- | --- | --- |
| Replacement edits overwrite or clear protected attributes | P1 | WFS explicit protected writes use create-style validation; WFS/OData/OGC API omission can clear protected fields. Shared replacement preservation and WFS update validation are under remediation in #6988. gRPC has a downstream partial-merge mitigation. |
| Publication tenant visibility omitted by protocol paths | P1 | Confirmed in discovery, search, render/tile, related-record, replication and service-wide selection paths across Records, OData, OGC Maps, WMS/WMTS, ImageServer, MapServer, VectorTileServer, PMTiles and FeatureServer. Existing resource/service access checks omit publication tenant scope. Filtering must precede primary selection/deduplication and cache access. OData regressions reproduce the defect; repair and finite call-site census in progress in #6989. |
| Published forms accept writes forbidden by current field policy | P1 | Package-time editability can outlive current resource restrictions. Current schema enforcement is under remediation in #6991. |
| OGC API PATCH removes protected attributes | P1 | Null-valued submitted properties can bypass field validation; unchanged protected attributes in the merged row must not be rejected as new writes. Regression and shared policy repair in #6996. The separate `properties: null` whole-properties behavior already has a mapping and remains a control case. |
| SAML audience restrictions incorrectly flattened | P2 | Validator accepts any audience across groups instead of requiring each restriction to match. Requires an otherwise valid signed assertion. Issue #6987. |
| SAML local replay claim not atomic | P2 | Memory-cache factory can execute concurrently for the same assertion; Redis NX path is unaffected. Issue #6987. |
| Import progress read failure skips leadership check | P2 | Monitor catches failed progress reads before reaching heartbeat. Target advisory lock mitigates duplicate writes but does not stop stale execution. Fixed in draft PR #6995 (`3d3c8813f3`); regression failed on baseline, 13 focused tests pass after correction; primary independent review complete. Required gates pending. |
| Import replacement cascades into independent dependencies | P1 | Target ownership check does not authorize deleting dependent views/materialized views or foreign keys. Issue #6993 adds fail-closed replacement and database regressions. |
| Version-job fallback loses newer state after Redis recovers | P2 | A stale existing Redis record wins over newer node-local fallback state. Tracked in #6997; remediation deferred. |
| Reconciler renewal exception leaves reconciliation running | P2 | Renewal task faults without cancelling its linked token; awaiting the fault also skips release. Submission CAS mitigates duplicate submission. Tracked in #6990; remediation deferred. |

No supported P0 finding so far. Finding counts are not evidence of complete coverage.

## Coverage ledger

| Surface | Assessment | Remaining evidence |
| --- | --- | --- |
| Shared auth, tenant header overrides, layer policy, job/artifact ownership | Source review | Runtime adversarial tests and exhaustive ingress variants |
| URL imports, pinned DNS, webhook transport | Source review | Network fault and redirect behavior in deployed configuration |
| GDAL process arguments, archive containment, extraction limits | Source review | Output amplification hypothesis remains unproven; no runtime exploit claim |
| SAML assertion validation and replay | Source review, regressions in progress | Fix and independent re-review |
| WFS mutation pipeline and Postgres writer | Source review, regressions in progress | Protected-field semantics including masked attributes |
| Postgres mutation transactions and outbox | Source review | Fault injection and other providers |
| Import leadership and cancellation | Fixed, baseline failure and 13 passing regressions | Required gates on PR #6995 |
| Version jobs and execution reconciler leases | Partial source review | Recovery tests and durable dispatch/sweeper coverage |
| Output-cache credential bypass, anonymous policies, metadata/tile keys | Sampled source review | Exhaustive endpoint-policy registration and invalidation races |
| SQL Server identifiers and filter construction | Sampled source review | Other provider/query combinations and runtime provider tests |
| Migration runner compatibility/contract gates | Partial source review | Migration SQL completeness and upgrade execution |
| Release security pipeline | Existing CI evidence inspected | Fresh evidence tied to final candidate and artifact digests |
| SOLID/DRY cleanup | Existing work #6934 identified | Broader behavioral duplication and provider contract review |

## Validation

- Baseline `Honua.Architecture.SourceTests`: **121 passed, 0 failed, 0 skipped**, Release configuration, through required dotnet build-slot shim.
- Existing Security Nightly run [41996766600](https://github.com/honua-io/honua-server/actions/runs/41996766600) passed NuGet, filesystem, notices, and AOT Alpine/JIT Debian container jobs on ancestor `f0b09c4cf765149810b0851d31b31c09f1327843`. This is not final-candidate verification.
- Vulnerability-gate parser: **7 passed**; immutable-image receipt controls: **6 passed**. These validate gate logic, not the application dependency inventory.
- GitHub open Dependabot inventory: two moderate action-tooling advisories, `GHSA-g3hv-75wg-m249` (minimatch) and `GHSA-3g3m-2fjr-6pw4` (glob), both in `.github/actions/managed-test-artifacts/package-lock.json`; the action passes fixed manifest/archive paths to the artifact client rather than caller-supplied glob patterns. No P0/P1 exploit path established; dependency remediation remains tracked by Dependabot.
- Graph search timed out; findings were checked against source directly.
- CSO helper failed snapshot capture with `SNAPSHOT_RACE`; CSO assessment/scanner assurance is **not assessed**. Conventional static security review is separately identified above.
- Full matrix, deployment checks, cloud-provider integration, current dependency advisory inventory, and final integrated candidate review are outstanding.

## Exit criteria

No confirmed or unresolved potentially P0/P1 finding; high-risk coverage gaps resolved; fixes independently reviewed; regression and required gates passing on integrated candidate; final independent pass finds no new P0/P1. Lower-priority findings retain explicit disposition. Publishing the release is outside this audit.
