# Audit platform-20261006 — issue 5622

Current-code verification was performed against this branch before making changes. The
highest-severity finding was completed first; the two lower-severity findings remain for
follow-up rather than being addressed shallowly in the same fix unit.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-DB-009` | partially fixed; review P1 remains open | `Finding_SRV_DB_009_GraphIdsAreUniqueAcrossDatabaseConnections` proves that equal per-database layer numbers produce distinct resource and storage-binding identities. Graph IDs incorporate the effective storage connection UUID; managed storage retains legacy IDs even when the source route supplies a connection UUID, covered by `UpsertPublishedLayerMetadataV2Async_UsesEffectiveStorageConnectionForGraphIdentity`. Numeric protocol layer identifiers remain unchanged. Lifecycle and extent mutations still select numeric IDs across connections; the complete repair exceeds the review packet's approximately 100-line limit and must carry qualified identity through the service contract and tenant validation. |
| `SRV-DB-019` | not attempted | Re-verification found that `PersistMetadataV2MutationAsync` still sends one `SaveAsync` call and only handles an unknown commit outcome; it does not reload and rebuild a mutation after `MetadataV2GraphConcurrencyException`. A correct bounded rebase requires restructuring all mutation builders and was deferred so the S1 fix could be completed and tested first. |
| `SRV-DB-024` | not attempted | Re-verification found that `PostgresMetadataV2GraphStore.RefreshSidecarsAsync` still loops over each complete graph collection and executes one insert command per object while the save transaction holds the environment advisory lock. Bulk sidecars and safe snapshot retention require a separate database migration/performance change and were deferred behind the S1 correctness fix. |

PR #5665 review adjudication: P2 is fixed by deriving resource, binding, publication,
and connection dependencies from effective storage ownership. The publish-path theory
covers both managed and source storage, graph references, and numeric protocol IDs.
P1 is confirmed at `BuildLayerEnabledMetadataV2Graph` and
`SyncRefreshedExtentsIntoV2GraphAsync`; their callers pass numeric IDs without the
route connection UUID. `SelectPublicationMetadata` also uses numeric selectors for
tenant checks, so a partial builder-only change would leave the contract inconsistent.
The P1 review thread remains unresolved under the packet's stop rule.
