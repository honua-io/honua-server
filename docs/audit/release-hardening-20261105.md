# Production GA hardening audit

Status: **partial — not a GA clearance**.

Tracking: https://github.com/honua-io/honua-server/issues/6985

Baseline: `800c0ec9277733433f90cd56e6e53f35bda1b11b` (`origin/trunk`, fetched 2026-11-05). The shared checkout contains unrelated uncommitted work and is excluded. Three Sol review lanes cover security, design, and reliability; findings receive primary review and independent fix review.

## Findings under remediation

| Finding | Severity | Evidence and disposition |
| --- | --- | --- |
| Replacement edits overwrite or clear protected attributes | P1 | WFS explicit protected writes use create-style validation; WFS/OData/OGC API omission can clear protected fields. Shared replacement preservation and WFS update validation are under remediation in #6988. gRPC has a downstream partial-merge mitigation. |
| Publication tenant visibility omitted by protocol paths | P1 | Confirmed in discovery, search, render/tile, related-record, replication and service-wide selection paths across Records, OData, OGC Maps, WMS/WMTS, ImageServer, MapServer, VectorTileServer, PMTiles, StaticMap and FeatureServer. Existing resource/service access checks omit publication tenant scope. Filtering must precede primary selection/deduplication and cache access. OData and imagery regressions reproduce the defect; WMS/WMTS eight-case suite passes. Imagery repair is in draft PR #7025; remaining protocol repair and finite call-site census in progress in #6989. |
| Published forms accept writes forbidden by current field policy | P1 | Package-time editability can outlive current resource restrictions. Current schema enforcement is under remediation in #6991. |
| OGC API PATCH removes protected attributes | P1 | Null-valued submitted properties can bypass field validation; unchanged protected attributes in the merged row must not be rejected as new writes. Regression and shared policy repair in #6996. The separate `properties: null` whole-properties behavior already has a mapping and remains a control case. |
| Existing-row RLS missing from canonical writes | P1 | Forms expose batch update/delete paths without original-row visibility checks; provider batch validation checks only the resulting row, and single-feature methods bypass the check. Same-transaction original-row and resulting-row enforcement under remediation in #6999. |
| Vulnerable SQLite native runtime in production GeoPackage import | P1 | Resolved native package2.1.11 reports SQLite3.49.1; production opens uploaded databases despite a test-only suppression rationale. Independently reviewed source exposure to stored view SQL, without executing an exploit. Maintenance bundle 2.1.14 (native SQLite 3.54.0) and suppression removal in draft PR #7022; baseline runtime regression confirmed; CI native runtime floor and six GeoPackage preview/import unit cases pass. Full packaging and final integrated validation remain pending. |
| OIDC audience identities compared without case sensitivity | P1 | Bearer, portal and provider-narrowed validators use case-insensitive custom audience checks; an otherwise valid token for a case-distinct audience can match. Source-confirmed defect under #7023; independently reviewed fix and signed-token regressions in draft PR #7027, runtime verification queued. Requires a trusted issuer with distinct opaque audiences. |
| SAML audience restrictions incorrectly flattened | P2 | Validator accepts any audience across groups instead of requiring each restriction to match. Requires an otherwise valid signed assertion. Two signed assertion negatives fail on baseline; independently reviewed repair passes the 23-test SAML suite in draft PR #7026 (issue #6987). |
| SAML local replay claim not atomic | P2 | Memory-cache factory can execute concurrently for the same assertion; Redis NX path is unaffected. Concurrent-miss regression yields two successful claims on baseline; independently reviewed repair passes the 23-test SAML suite in draft PR #7026 (issue #6987). |
| Import progress read failure skips leadership check | P2 | Monitor catches failed progress reads before reaching heartbeat. Target advisory lock mitigates duplicate writes but does not stop stale execution. Fixed in draft PR #6995 (`3d3c8813f3`); regression failed on baseline, 13 focused tests pass after correction; primary independent review complete. CI PR Gate, Build/tests, formatting, CodeQL, affected shards and runtime proofs passed. Review Gate declines draft status as expected. |
| Import replacement cascades into independent dependencies | P1 | Target ownership check does not authorize deleting dependent views/materialized views or foreign keys. All six real database regressions fail on baseline; #6993 implements fail-closed replacement, with green checks pending. |
| Version-job fallback loses newer state after Redis recovers | P2 | A stale existing Redis record wins over newer node-local fallback state. Tracked in #6997; remediation deferred. |
| Reconciler renewal exception leaves reconciliation running | P2 | Renewal task faults without cancelling its linked token; awaiting the fault also skips release. Submission CAS mitigates duplicate submission. Tracked in #6990; remediation deferred. |

Eight confirmed P1 finding families are tracked above; none is closed without verification. A ninth potential P1, #7028, is under validation: managed resources sharing one actual storage ID can resolve the first resource’s row/field policy instead of the resource authorized by the route. Real-Postgres routed regressions include a noncolliding policy control and denial of the admin-only resource; runtime evidence is pending. No supported P0 finding so far. Finding counts are not evidence of complete coverage.

## Coverage ledger

| Surface | Assessment | Remaining evidence |
| --- | --- | --- |
| Shared auth, tenant header overrides, layer policy, job/artifact ownership | Source review | Runtime adversarial tests and exhaustive ingress variants |
| URL imports, pinned DNS, webhook transport | Source review | Network fault and redirect behavior in deployed configuration |
| GDAL process arguments, archive containment, extraction limits | Source review | Output amplification hypothesis remains unproven; no runtime exploit claim |
| SAML assertion validation and replay | Independently reviewed fix; 23 tests pass in draft PR #7026 | Integrated candidate verification |
| WFS mutation pipeline and Postgres writer | Source review, regressions in progress | Protected-field semantics including masked attributes |
| Postgres mutation transactions and outbox | Source review | Fault injection and other providers |
| Import leadership and cancellation | Fixed, baseline failure and 13 passing regressions | PR Gate passed on PR #6995; Review Gate intentionally blocked by draft status |
| Version jobs and execution reconciler leases | Partial source review | Recovery tests and durable dispatch/sweeper coverage |
| Output-cache credential bypass, anonymous policies, metadata/tile keys | Sampled source review | Exhaustive endpoint-policy registration and invalidation races |
| SQL Server identifiers and filter construction | Sampled source review | Other provider/query combinations and runtime provider tests |
| Migration runner compatibility/contract gates | Partial source review | Migration SQL completeness and upgrade execution |
| Release security pipeline | Existing CI evidence inspected | Fresh evidence tied to final candidate and artifact digests |
| SOLID/DRY cleanup | Existing work #6934 identified; supported duplicated OIDC token-policy construction added to campaign | Broader behavioral duplication and provider contract review |
| Live subscriptions and fan-out | Source review: feature delivery checks publication tenant and refreshed row/field policies; STA compares tenant/schema scope; admin SignalR requires admin authorization; collaboration join checks draft and parent owner/tenant | Runtime adversarial transport tests; feature-stream discovery/initial selection remains a separately tracked lower-priority concern |

## Validation

- WMS/WMTS publication regression: baseline **6 failed, 2 passed** on intended security assertions after correcting fixture registrations; reviewed repair **8 passed, 0 failed** in Release. ImageServer resolver/current-binding regression: **8 failed, 6 passed** on baseline; an initially non-discriminating tile fixture was corrected and fails the expected refusal assertion on baseline. Complete ImageServer green verification is queued.
- SAML baseline **3 failed, 20 passed**: two signed audience-restriction negatives and concurrent local replay claim. Independently reviewed repair: **23 passed, 0 failed, 0 skipped**; draft PR #7026.
- Protected fields/RLS baseline: **15 real database failures** reproduce four versioned replacement losses, four default replacement losses, four hidden-row batch writes, and three direct-writer policy bypasses. Additional forms, PATCH and versioned RLS cases are queued; no green claim yet.
- Publication selection census: all **107 enumerated files** have source-backed dispositions, with no unadjudicated entry. This finite census is not exhaustive application or runtime coverage; protocol/provider and metadata-race qualifications remain.
- Baseline `Honua.Architecture.SourceTests`: **121 passed, 0 failed, 0 skipped**, Release configuration, through required dotnet build-slot shim.
- Existing Security Nightly run [41996766600](https://github.com/honua-io/honua-server/actions/runs/41996766600) passed NuGet, filesystem, notices, and AOT Alpine/JIT Debian container jobs on ancestor `f0b09c4cf765149810b0851d31b31c09f1327843`. This is not final-candidate verification.
- Vulnerability-gate parser: **7 passed**; immutable-image receipt controls: **6 passed**. These validate gate logic, not the application dependency inventory.
- GitHub open Dependabot inventory: two moderate action-tooling advisories, `GHSA-g3hv-75wg-m249` (minimatch) and `GHSA-3g3m-2fjr-6pw4` (glob), both in `.github/actions/managed-test-artifacts/package-lock.json`; the action passes fixed manifest/archive paths to the artifact client rather than caller-supplied glob patterns. No P0/P1 exploit path established; dependency remediation remains tracked by Dependabot.
- Graph search timed out; findings were checked against source directly.
- CSO helper failed snapshot capture with `SNAPSHOT_RACE`; CSO assessment/scanner assurance is **not assessed**. Conventional static security review is separately identified above.
- SQLite exposure/update evidence: [upstream vulnerability model](https://www.sqlite.org/cves.html), [researcher advisory](https://github.com/google/security-research/security/advisories/GHSA-qj7j-3jp8-8ccv), and [maintenance update](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/2.1.14). The obsolete suppression guard fails before its removal and all seven gate-parser tests pass afterward. The baseline .NET connection also reproduces SQLite 3.49.1 below the required 3.50.2 floor. The maintenance package native linux-x64 binary reports 3.54.0 and includes Linux x64/ARM64 assets. PR #7022 CI job `127656881064` passed 1,416 Postgres unit tests; its TRX confirms `Open LoadedNativeEngine IncludesAggregateBoundsFix` and six GeoPackage preview/import cases passed. This confirms the loaded .NET native floor and those compatibility paths, not every runtime image or export path. PR #7022 NuGet Vulnerability Scan passed with zero critical, high, or suppressed advisories; the CI router is blocked by the separately tracked missing AI test shard repaired in PR #6994.
- Full matrix, deployment checks, cloud-provider integration, final dependency inventory, and final integrated candidate review are outstanding.

The integration branch `audit/ga-hardening-integration` currently combines committed SQLite, import-monitor, SAML and imagery changes on trunk `f1b472433e`. They cherry-pick without conflicts. This is preparation, not integrated test clearance. The intervening service-folder change received a targeted source review of replay authorization, tenant scoping and reservation persistence; it has not received an exhaustive independent audit here.

## Exit criteria

No confirmed or unresolved potentially P0/P1 finding; high-risk coverage gaps resolved; fixes independently reviewed; regression and required gates passing on integrated candidate; final independent pass finds no new P0/P1. Lower-priority findings retain explicit disposition. Publishing the release is outside this audit.
