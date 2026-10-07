# Audit platform-20261006 — issue 5622

Current-code verification was performed against this branch before making changes. The
highest-severity finding was completed first; the two lower-severity findings remain for
follow-up rather than being addressed shallowly in the same fix unit.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-DB-009` | fixed, including review P1 | `Finding_SRV_DB_009_GraphIdsAreUniqueAcrossDatabaseConnections` proves that equal per-database layer numbers produce distinct resource and storage-binding identities. Graph IDs incorporate the effective storage connection UUID; managed storage retains legacy IDs even when the source route supplies a connection UUID, covered by `UpsertPublishedLayerMetadataV2Async_UsesEffectiveStorageConnectionForGraphIdentity`. Numeric protocol layer identifiers remain unchanged. Connection-qualified overloads now carry storage ownership from admin routes through lifecycle, linking, extent synchronization, response governance, and tenant validation. The SQL catalog identifies managed handles, which use null connection ownership after managed-store verification. Equal numeric storage handles on other connections are excluded. Extents resolve through StorageLayerId and ResourceId rather than protocol LayerIndex. |
| `SRV-DB-019` | not attempted | Re-verification found that `PersistMetadataV2MutationAsync` still sends one `SaveAsync` call and only handles an unknown commit outcome; it does not reload and rebuild a mutation after `MetadataV2GraphConcurrencyException`. A correct bounded rebase requires restructuring all mutation builders and was deferred so the S1 fix could be completed and tested first. |
| `SRV-DB-024` | not attempted | Re-verification found that `PostgresMetadataV2GraphStore.RefreshSidecarsAsync` still loops over each complete graph collection and executes one insert command per object while the save transaction holds the environment advisory lock. Bulk sidecars and safe snapshot retention require a separate database migration/performance change and were deferred behind the S1 correctness fix. |

PR #5665 review adjudication: both P1 and P2 are real and repaired. P2 derives
publish identities from effective storage ownership. P1 carries the same ownership
through subsequent mutations and authorization. Named admin routes pin one connection
UUID for both credential resolution and graph scope. Existing service overloads remain
available for server-storage callers; tenant checks without an explicit connection
retain their conservative legacy behavior. Lifecycle changes still reach all publications
of the selected binding, and shared-resource lifecycle still derives from its bindings.
Extent synchronization follows physical storage handles even when an authored protocol
publication uses a different layer index.

New regressions cover equal numeric handles on two source connections plus managed
storage, isolated disable/re-enable and linking, tenant checks, and the actual extent
synchronization save. The route regression covers UUID and named connection paths.
Validation checkpoint: the SQL graph and publication namespace suites passed
92/92 tests with no skips. Core, PostgreSQL, Server, TestKit, and both changed test projects built
with zero warnings and errors. All six project-scoped formatting checks passed
for the changed files under `timeout 20m`. The focused server route and database
integration regressions passed 9/9 tests with no skips: named/UUID connection
pinning, existing exception mapping, and source/managed disable-one, enable-all,
and extent persistence. The integration assertions read the fixture's request
schema partition through its snapshot helper. No existing tests were weakened
or removed.

PR Gate run `37638172859` rejected head
`e9d5c711fc5d231e5fd7c2ba5594889d246ff16b` for whitespace in the two new
publishing test fixtures and a stale operation-matrix source digest. The digest
matched base `7c422ec37e6bc647ecae7bc231e71b0d88979fbf` exactly; this was
PR-local evidence drift, not trunk or runner breakage.

The bounded operation-matrix re-audit covers `import.dataset`. Only
`ImportDatasetJobExecutor.cs` changed within the catalog source roots: its XML
reference disambiguates the existing overload, and extent refresh now receives
the same parsed connection UUID that publication already uses. Named references
retain their existing null ownership on both calls. Import, flattening,
progress, provenance, cancellation and best-effort refresh error handling are
unchanged. The provider refresh still recomputes SQL layer/service extents and
now synchronizes graph bounds through connection-qualified physical storage
bindings, including the verified managed-store exception.

The existing `ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance`
receipt still executes production import/publishing services against PostGIS and
asserts the published table, layer extent and decoded provenance feature count
and layer ID. Catalog membership, declared entry points, the other 97 operation
implementations and all evidence method bodies are unchanged. Their verdicts and
shared runtime gaps are retained. The refreshed digest records this source audit;
the architecture guard and every test assertion remain intact.
