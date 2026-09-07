# Alerting Preview floor evidence (#4426)

The 2026.1 Preview amendment retains durable mutation auditing and fail-closed
tenant isolation. Preview does not release either floor. This change keeps the
instance-wide alert stores inaccessible to tenant-scoped administrators and
commits each lifecycle mutation together with its persisted audit record on one explicitly owned Postgres connection and transaction. Secure connection resolution finishes before that transaction starts; connection-string auto-enlistment is not required.

## Independent fixture expectations

`AlertPreviewFloorTests` runs HTTP requests against the migrated Postgres store
and reads the persisted rows back. Its expectations are specified by the fixture,
not captured from the implementation's current output:

- The zone is a square with vertices `(0,0), (0,2), (2,2), (2,0), (0,0)`.
  Its independently calculated area is `2 * 2 = 4`; stored ordinates and SRID
  `4326` are asserted after updating it.
- The rule references the created zone and layer `1`. Updated names, both
  enabled states, and deletion of both rows are checked through the real store.
- Eight persisted audit rows must appear in the requested lifecycle order:
  zone create/update, rule create/update/disable/enable/delete, zone delete.
  Each row must have the fixture's seeded principal ID, `ApiKey` actor type,
  `ConfigChange`, `Success`, a bounded timestamp, the caller-generated correlation
  ID, the correct resource identity, and the expected service/layer/zone details.
- A real Postgres trigger rejects audit INSERTs for one uniquely correlated
  request in each of eight action cases. The HTTP result must be `503`, stored
  names/geometry/SRID/active states and row counts must remain unchanged, and no
  audit row may survive. Each test removes its own trigger and function.
- Named secure connections (with a real, separate registry connection) and direct connections configured with `Enlist=false` must both roll back a rejected audit INSERT and commit a subsequent successful mutation with exactly one matching persisted audit row. Both `/rules/test` and `/rules/test/` remain read-only and work without a persisted audit sink.
- A sink that returns no audit identity also causes `503` and rollback of real
  persisted changes. An audit receipt is required to commit.
- Omitted or blank service scope returns `400`; an explicit scope returns only
  its fixture's rows. Anonymous and tenant A/B requests cannot disclose or mutate
  instance alert data. Claim checks also cover disabled tenant resolution and
  mixed/uppercase tenant claim names.

## Native execution receipt

Executed on 2026-09-06 using the Windows .NET SDK 10.0.100, Release configuration,
`-maxcpucount:4`, and Docker Desktop PostGIS 18 / PostGIS 3.6 fixtures.

| Check | Result |
| --- | --- |
| Affected alert endpoint, operations, floor, and isolation suites, including all four review regression cases | 52 passed, 0 failed, 0 skipped |
| Postgres audit persistence, chain, retention, and truncation suites | 26 passed, 0 failed, 0 skipped |
| MCP taxonomy and capability registry conformance | 68 passed, 0 failed, 0 skipped |
| Server Fast suite plus final request-disposal regression | 4079 passed, 0 failed, 0 skipped |
| Full architecture after explicit transaction changes and catalog regeneration | 287 passed, 0 failed, 0 skipped |
| Tenant claim casing regression before the fix | 2 failed, 6 passed; both failures reached the forbidden instance-store delegate |
| Changed-file `dotnet format` and `--verify-no-changes` | Passed |

The Server and Architecture projects compiled with zero warnings/errors. The
solution build initially caught CA1859 in an existing cache test; the same
concrete-variable correction already present on trunk is included here. The
broad alert run above includes a successful rebuilt Server.Tests assembly.
The full architecture run passed 286 checks and identified one missing endpoint
annotation; the final 15-check run above verifies that correction. Native Fast
validation also required canonical LF output in the parity-export generator and
coordinated renewal-file access and asynchronous shutdown in the license fixture.
All renewal assertions remain intact. One intermediate Fast run hit the existing streaming cancellation deadline race under concurrent native load (4077 passed, 1 failed); the unchanged streaming assertion passed in the final full Fast rerun above. Documentation validation passed 2056 link
targets and 13 code-referenced anchors, with 14 pre-existing allowlisted warnings.

See the [alert rule design](../design/realtime-alert-rules.md) for the endpoint contract.
The required PR gate runs `AlertPreviewFloorTests` in its Server governance step.
The feature catalog is generated with `FeatureCatalogEmitter`; it is never
hand-edited. Final catalog/architecture verification and head-specific CI results
are recorded in the pull request.
