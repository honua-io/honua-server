# Issue 5614: local hidden-field recovery

Owner: Mike McDougall <mike@honua.io>. One writer, one issue, one PR.

The current issue and trunk were read before editing. No existing PR or recovery
ref was found; a second search immediately before final verification found no
competing PR. Starting trunk: `5d2ffbb7cd34739b76f6326db5991b01ae2079a4`.
Publication reconciliation merged new trunk
`e9eee40b5f794c885ae44d98a76fa388443cee8d` cleanly (gated-boot shutdown).
The packet implementation and tests were unchanged by the merge; the normal
reference build and the complete focused selections were repeated afterward. The parked cloud packet
remains historical; READY and historical test claims are not delivery evidence.

## Dispositions

| Finding / path | Starting-trunk evidence | Final disposition |
| --- | --- | --- |
| SRV-OGC-007: GML collection/single/stream; CSV collection/single; queryables; properties, sort, queryable parameters, CQL text/JSON; optimized raw GeoJSON and bbox point output | 13 failures in `ogc-red.trx` | Fixed: final OGC regressions pass |
| Ordinary OGC GeoJSON collection/single | 2 passes in the same baseline | Already fixed; retained regression coverage |
| WFS GML/CSV GetFeature, DescribeFeatureType, PROPERTYNAME, SORTBY, GetPropertyValue and FES reference | 7 failures in `wfs-red.trx` | Fixed: final WFS regressions pass |
| WFS GeoJSON | 1 pass in the same baseline | Already fixed; retained regression coverage |
| SRV-GRPC-003: unary/streaming field definitions and attributes, default/wildcard/explicit out-fields | 6 failures in `grpc-red.trx` | Fixed: final gRPC regressions pass |

Not reproduced: none of the completed original checks. Not attempted: optional
gRPC where/order hidden-field policy, non-Postgres provider integration and full
CITE qualification. These limits are separate from the required reproduced
output/query-binding corrections. The issue remains open until normal delivery
and merge; required items are not represented as complete by a READY status.

## Supported corrections and compatibility

- OGC selects visible GML/CSV/raw GeoJSON attributes and queryables, binds client
  property/filter/sort references against visible schema, and defensively removes
  hidden keys from provider GML results, including streaming output.
- A candidate run exposed a built-in sort allow-list bypass for hidden
  `created_at`, including descending uppercase spelling. Both tests first failed
  and now pass after excluding declared-hidden core fields.
- The original schema remains available for custom public feature ID binding.
  A Docker-backed regression first exposed collection ID disagreement and a
  hidden identifier property. Collection and item IDs now agree; response-owned
  GeoJSON properties omit the hidden identifier without changing top-level IDs.
  This correction is in the OGC adapter; the shared builder is untouched.
- WFS uses visible schema for output/schema generation and property/FES binding.
  A transaction regression first returned HTTP 400 after read filtering also
  affected input resolution. Explicit transaction binding now preserves declared
  hidden inputs; the regression verifies the stored value directly in PostGIS.
- gRPC excludes hidden response field definitions and attribute keys, including
  explicit out-fields, for unary and streaming responses. Case-insensitive
  provider-extra-attribute regressions cover gRPC conversion and all four GML
  formatter entrypoints. The GML streaming integration requires 300 members.

Hidden remains publisher presentation metadata, not field-level access control.
Existing provider-enforced masks and visible-field behavior remain covered.
No tests were weakened, skipped or deleted; no credentials, secrets, variables
or hosted gates changed. All production/test edits are within packet TOUCHES.

## Local verification

All integration receipts use actual local Docker (29.8.0), `WebAppFixture` and
`PostgresFixture`, Testcontainers image `postgis/postgis:18-3.6`, isolated schemas
and seeded rows. Receipts are retained at
`/home/mike/honua-io/5614-evidence/`; command logs are alongside that directory.

Final reconciled-trunk candidate: **273 passed, 0 failed, 0 skipped**:

- OGC: 65. Hidden output/query/identifier regressions, provider GML filtering,
  existing raw and streaming GeoJSON, formatter and base-builder tests, and
  visible property/sort/CQL text/JSON integration.
- WFS: 21. Hidden output/query/transaction regressions, property resolver tests,
  visible QName round trip, schema attribute types and wildcard projection.
- gRPC: 187. Hidden unary/streaming output and conversion regressions, existing
  feature-service, conversion and masked-field predicate tests.

Commands used PATH `dotnet test <protocol-test-project> --no-build --no-restore
--filter <selection> --logger trx --results-directory <evidence-directory>`.
Selections were the named regression classes and existing classes/methods above;
TRX test definitions retain the exact executed case list. Production DLL hashes
were checked against each test output before running.

The OGC/gRPC baseline test assemblies reused unchanged starting-trunk production
binaries; WFS built normal references. Candidate normal-reference Debug builds
passed with warnings treated as errors. Pre-reconciliation changed-project builds also passed
with warnings as errors, reusing identical unchanged dependency builds. After
the new trunk merge, all three protocol test projects and their normal project
references rebuilt successfully in Debug with warnings treated as errors. No
Release/full-solution/architecture/CITE pass is claimed. All changed projects
were formatted with `--include` for changed files, `timeout 20m`, and explicit
shared-slot admission. All builds/tests used the PATH lane shim, the four shared
build slots, CPU cap and shared Roslyn compilation. Unrelated holds remain.

A host interruption delayed a requested 20-second wait by over seven minutes.
The first reconciled OGC run recorded 64 passes and one existing keep-alive test
HTTP timeout (its unchanged 15-second limit). That failed receipt is retained;
the unchanged complete 65-case selection subsequently passed. No test timeout,
assertion, selection or skip setting was relaxed. Earlier green receipts are
also retained separately from the final reconciled-trunk receipts.

## Review and recovery

The single self-review and independent review froze two original P1 findings in
203 seconds: retain original schema for public ID binding, and document new
public test types. Both originals are retained and marked fixed only after the
final focused tests passed. Independent review also validated response dictionary
ownership, ID preservation and completed XML summaries. No P0/P1 remains;
no P2/P3 finding was identified. Original review artifacts remain in
`/tmp/self-review-fix-5614-hidden-field-local-recovery`.

The initial branch and successive WIP checkpoints were pushed for recovery;
intermediate work went to `wip/fix/5614-hidden-field-local-recovery`. The PR
branch receives one consolidated reviewable update after verification. Normal
hosted admission/build/test/governance gates remain enabled. Hosted gate results
are not local evidence and are not awaited under this packet's delivery contract.

## Receipt hashes

| Receipt | Passed | Failed | Skipped | SHA-256 |
| --- | ---: | ---: | ---: | --- |
| `ogc-red.trx` | 2 | 13 | 0 | `faf627d09ddc0da506d7bd79ef6d8d64de0cbbf7df8b4a9ed5ebc6fcbdb8589a` |
| `wfs-red.trx` | 1 | 7 | 0 | `306577c6e337c18d404e78065d9cea90fc5d59e60ebc30409609742e44158a9c` |
| `grpc-red.trx` | 0 | 6 | 0 | `aa7d979856765517383190120f454facdf5bb3a0959ee6fbed0028ec68b46c29` |
| `ogc-candidate-core-sort.trx` | 19 | 2 | 0 | `3de682d2324d92bdaa033dd09e05118dbabfcddb21b0485583a1657784b20acb` |
| `wfs-input-regression-red.trx` | 0 | 1 | 0 | `1e06a1706d9ccb32998d65e8bf8d9fa1db22d2eab7241c589cd0ffe429c38dfb` |
| `ogc-identifier-both-red.trx` | 0 | 1 | 0 | `4f8ba1f49af628a373afe47325aa60299bd6dd43bdbddf630c3ebf159b989fd0` |
| `ogc-final-green.trx` | 65 | 0 | 0 | `7b7efe604d0bb2abbbcde7a536d074091265d0f012dc7c5bd86af65156afd5db` |
| `wfs-review-green.trx` | 21 | 0 | 0 | `e251d087e886f50dc9461a766e9e1ed902934e9f2e228d3e81e640b022121c27` |
| `grpc-review-green.trx` | 187 | 0 | 0 | `9a3b4fb0767553a8cfd863b29e8b21891275fd4b1ac2423c11a97ed28d509644` |
| `ogc-latest-trunk-interrupted.trx` | 64 | 1 | 0 | `e78a2875554e739c58651a45e653e8629dc4963206799f1f72ef62475574d759` |
| `ogc-latest-trunk-green.trx` | 65 | 0 | 0 | `159fd7d2dcd88fb98b9a8755f4384391d4699d82087eb6e7324cfe0631a08d48` |
| `wfs-latest-trunk-green.trx` | 21 | 0 | 0 | `67278d92b505eef83343c1906731f0a09384986b29e8502b183a8107adc9e9d7` |
| `grpc-latest-trunk-green.trx` | 187 | 0 | 0 | `e55a6d6d72aba674967d7c7b82b0d2bfd907d14c4a7d4a22303c6d9cfde8979a` |
