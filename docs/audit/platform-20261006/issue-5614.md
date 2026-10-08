# Issue 5614: local hidden-field recovery

Owner: Mike McDougall <mike@honua.io>. One writer, one issue, one PR.

The current issue and trunk were read before editing. No existing PR or recovery
ref was found. Starting trunk: `5d2ffbb7cd34739b76f6326db5991b01ae2079a4`.
The parked cloud packet remains historical; READY and historical test claims
were not treated as delivery or evidence.

## Runtime diagnosis

All integration receipts below use actual local Docker, `WebAppFixture` and
`PostgresFixture`, Testcontainers image `postgis/postgis:18-3.6`, isolated
schemas and seeded feature rows. Receipts are retained under the local
workspace `/home/mike/honua-io/5614-evidence/`; command logs are alongside it.

| Finding / path | Starting-trunk disposition | Receipt |
| --- | --- | --- |
| SRV-OGC-007: OGC GML collection, single item and streaming; CSV collection/single; queryables; properties, sort, queryable parameters, CQL text/JSON; optimized raw GeoJSON and bbox point output | Reproduced: 13 failed | `ogc-red.trx`, `5614-ogc-red.log` |
| Ordinary OGC GeoJSON collection/single output | Already fixed: 2 passed | Same OGC baseline |
| WFS GML/CSV GetFeature, DescribeFeatureType, PROPERTYNAME, SORTBY, GetPropertyValue and FES reference | Reproduced: 7 failed | `wfs-red.trx`, `5614-wfs-red.log` |
| WFS GeoJSON output | Already fixed: 1 passed | Same WFS baseline |
| SRV-GRPC-003: unary/streaming field definitions and attributes, default/wildcard/explicit out-fields | Reproduced: 6 failed | `grpc-red.trx`, `5614-grpc-red.log` |

No baseline case was skipped. Not reproduced: none of these completed checks.
The OGC/gRPC baseline test assemblies used `BuildProjectReferences=false`
against already-built, unchanged starting-trunk production binaries. The WFS
baseline built normal project references. Candidate verification builds normal
references with warnings treated as errors.

## Supported corrections in progress

- OGC filters hidden fields from CSV/queryables and raw GeoJSON selection,
  resolves query references against visible schema, projects visible GML
  attributes, and defensively removes hidden keys from provider GML results.
- WFS uses visible schema for output/schema generation and property/FES binding.
- gRPC excludes hidden response field definitions and removes hidden keys from
  unary/streaming attributes, including explicitly requested hidden fields.
- Case-insensitive provider-extra-attribute regressions cover all four GML
  formatter entrypoints and gRPC conversion. Streaming integration requires
  exactly 300 GML members after adding 300 rows.
- Candidate OGC run: 19 passed, 2 failed, 0 skipped (`ogc-candidate-core-sort.trx`).
  Declared-hidden `created_at` still returned HTTP 200 through the built-in sort
  allow-list, including descending uppercase spelling. The supported correction
  removes declared-hidden names from that allow-list; verification is pending.

These changes are unfinished until their candidate tests pass. Scoped formatting
completed for the changed OGC, WFS and gRPC projects; the additional OGC sort
regression is being formatted separately. Every build/test/formatter uses the
shared four slots, PATH dotnet and the MSBuild CPU cap. Unrelated holds remain.
No credentials, secrets, variables, tests or workflow gates were weakened.

Not attempted: gRPC where/order hidden-field policy (the issue describes this
as optional consideration), non-Postgres provider integration and full CITE
qualification. Focused candidate verification, latest-trunk reconciliation,
single self-review, independent review and normal hosted admission are pending.
Issue 5614 remains open while required acceptance is unfinished.

## Candidate receipts so far

- WFS: fixed for reproduced paths. `wfs-green.trx`: 20 passed, 0 failed,
  0 skipped. Includes 8 hidden-field cases, property resolver tests, visible
  QName round trip, schema attribute types and wildcard projection.
- gRPC: fixed for reproduced output paths. `grpc-green.trx`: 187 passed,
  0 failed, 0 skipped. Includes 6 Docker-backed unary/streaming hidden-field
  cases, case-insensitive conversion and existing feature-service, conversion
  and masked-field predicate tests.
- The normal-reference candidate build passed after adding the required XML
  parameter documentation. The subsequent supported OGC sort correction is
  rebuilding normally; final OGC verification remains pending.

Candidate input-preservation diagnosis: `wfs-input-regression-red.trx` has one
executed failure, HTTP 400 on an authorized update of hidden `category`. The
initial resolver correction also affected transaction binding. Transactions
now explicitly retain declared hidden input fields; read resolution remains
filtered. The regression checks the stored value directly and awaits green
verification. This preserves Hidden as a presentation rule, not a write ACL.

Final WFS receipt: `wfs-final-green.trx`, 21 passed, 0 failed, 0 skipped.
The hidden-field transaction update and direct stored-value assertion pass.
Independent review and the single self-review froze two original P1 findings
in 203 seconds: retain full schema for public identifier binding, and document
new public test types. XML summaries are added; a new Docker-backed hidden
custom identifier regression is running before the binding correction.
Neither review finding is marked fixed until focused verification passes.

The hidden custom identifier regression reproduced both collection ID binding
failure and a hidden identifier property in GeoJSON. The correction retains the
original schema for IDs and removes that hidden property from the response-owned
feature dictionary in the OGC adapter. All edits remain within packet TOUCHES;
no shared builder change is needed. Independent review validated these changes
and the XML summaries in source. Focused green verification is still required.
Scoped formatting for these final edits passed.
