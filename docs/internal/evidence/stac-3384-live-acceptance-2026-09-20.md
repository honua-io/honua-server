# STAC 3384 live acceptance — 2026-09-20

**Disposition: blocked; issue #3384 remains open.** The 2026.1 release promise
is working STAC/Records discovery and item search, with governed live client
compatibility evidence. Restored HTTP responses alone do not complete that
promise's deployment-binding and Python staging acceptance.

## Server delivery retained

[PR #4808](https://github.com/honua-io/honua-server/pull/4808) merged as
`f23f5a67012502f47e307715998c81578599774a`, an ancestor of the inspected trunk
`fc3074dcc3f3267fc4f07649db05ff02a073708c`. It fixes the incomplete Metadata v2
bootstrap document in `tests/seed/demo-stac-imagery-v1.sql` and supplies
`DemoStacSeedMigratedDatabaseTests` over the production migration runner and
real seed. The test asserts four independently specified scenes, coordinates,
datetime, properties, paging, spatial/collection filtering, and the retained
journal failure mode. No test was changed or disabled in this acceptance review.

The requested prior integration branch `codex/2026-1-server-release` was fetched
at `47609d19acdfa4877d8819072a4226e04825e7a2`. Its 274 changed paths relative to
its trunk merge base contain no STAC or seed changes. The newer issue-specific
branch `fix/3384-stac-demo-collection-regression` at `727fc1227e` is newer than
its `wip/` checkpoint `12ea1de4c7`. This review resumed that successor and
rebased onto trunk; Git recognized its fix as already applied by #4808.

## Live observations

The [September 16 restore receipt](https://github.com/honua-io/honua-demo-infra/pull/79#issuecomment-5703693781)
records a guarded schema move of the existing relation, with 110,229 rows and
unchanged content digest and change journal. The rows had been in the wrong
schema; the earlier assumption that no recoverable rows existed was superseded.

At 2026-09-20 23:04 UTC, independent public HTTP probes compared the response to
the four literal scenes specified in the regression fixture:

| Request | HTTP | Expected and observed IDs |
| --- | --- | --- |
| Collection 90810 items, limit 25 | 200 | 9081001, 9081002, 9081003, 9081004 |
| POST search, collection 90810, limit 25 | 200 | 9081001, 9081002, 9081003, 9081004 |
| POST search, same collection, bbox `[-156.70,20.85,-156.45,20.96]` | 200 | 9081001, 9081003 |

Counts, collection IDs, Point geometry with exactly two ordinates, longitude,
latitude, datetime, name, quality score, and platform matched the fixture.
The full comparison **failed**: live responses omit `eo:cloud_cover` and
`view:sun_azimuth`, which the current seed and regression test require. This is
not a successful replay of the current fixture on the deployed version. No
raster values or nodata were involved in these feature queries.

Response SHA-256 values, in table order:

- `4612587741fb85f63e041e56959c029089d8ad18b1f9b3e34368e9ea6116b140`
- `a53f4af8d9778a0f85f812521b8de593b784a1627e7b29c902fa4a3b39a20a22`
- `74e5de5dbc17230a56f3b5507f0a0a97fa516429d650c72f7bfa804a3c8cc15f`

## Scheduled green is insufficient for rotation

[Scheduled trunk canary 35536764819](https://github.com/honua-io/honua-demo-infra/actions/runs/35536764819)
at producer commit `2b1d4786f7842ef8366dab340e4537027c79ddfb` passed 29/29 public
probes, including both 90810 requests, and 3/3 protected probes with zero
protected skips. Its managed seed receipt steps were skipped because the
workflow restricts them to `workflow_dispatch`.

The downloaded artifact archive matched GitHub's digest:
`sha256:bb874fe9264d33df42a54319a41ecb77a2b5a1336acd07af40955cafb28b134d`.
The exact canonical deployment-document bytes matched the protected receipt:
`50669da03fdc866252540048a0cc3c4f04e771a4d1ce82ae6ad6de22d9506971`.
The descriptor digest is
`b7bbd52a47e17c454a37e760cee42c1c24acb20bed3f265a59036037add7599a`.

Those hashes establish artifact integrity, not live identity. The new document
still declares server `e58532138377006960516ba23333185be55040b2` and image
`sha256:4fb503be4eace130ed218c8f940382e824ab43016aa5aa4edcfbf4b2f81ed2c9`.
Read-only Lambda inspection instead found serving version 42, no weighted
routing, and resolved image
`sha256:d97baf44b17ba5b9537320281f721252ed12f1fb43bee001011d720f3ae7d622`.
The queried `HONUA_GIT_SHA` environment value was absent; no current source SHA
was inferred from that missing value. The deployment document is therefore not
eligible for truthful staging rotation despite its new September 27 expiry.

The Python staging variable still contains the old binding that expired on
2026-08-16. It was read for diagnosis and left unchanged. No credential was
read or rotated, and no green Python staging execution is claimed.

## Governed replay and concrete dependency

[Explicit trunk canary 35543459282](https://github.com/honua-io/honua-demo-infra/actions/runs/35543459282)
was dispatched with the previously documented deployed source revision
`f897700159e2791c9468c6ca85bb4e2a3a8d8433`. It failed at **Require managed seed
receipt binding** before public/protected probes. All three inputs were absent:

- `HONUA_DEMO_STAC_RECEIPT_ROLE_ARN`
- `HONUA_DEMO_STAC_RECEIPT_FUNCTION_NAME`
- `HONUA_DEMO_STAC_METADATA_ENVIRONMENT`

The first error is `Set HONUA_DEMO_STAC_RECEIPT_ROLE_ARN from Terraform output`.
This is retained failing evidence; the scheduled event was not substituted for
the governed replay.

The next deploy action is already concrete in the
[main-stack plan summary](https://github.com/honua-io/honua-demo-infra/pull/79#issuecomment-5703775580).
The [operator boundary](https://github.com/honua-io/honua-demo-infra/pull/79#issuecomment-5704073327)
requires the operator workstation's `pass` source for the admin password and a
new saved plan. The previous placeholder-password plan must not be applied.
After that apply, the recovery procedure continues through the migration
runner, migrations, seed proof, and receipt configuration. None of those live
mutations was performed in this review.

Before closing #3384, complete the governed replay, verify/reconcile the
producer's immutable server/image lineage against the serving runtime, rotate
the exact verified non-secret document into Python staging, and obtain a green
remote staging receipt with `local_stack=false` and the same digest chain.
Keep the original RC2 failures linked from the issue. This remainder is an
operator/deployment dependency, not a criterion released because a candidate
does not exist.
