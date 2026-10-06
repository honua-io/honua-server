# Audit platform-20261006 — issue 5622

Current-code verification was performed against this branch before making changes. The
highest-severity finding was completed first; the two lower-severity findings remain for
follow-up rather than being addressed shallowly in the same fix unit.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-DB-009` | fixed | `Finding_SRV_DB_009_GraphIdsAreUniqueAcrossDatabaseConnections` proves that equal per-database layer numbers produce distinct resource and storage-binding identities. `PostgreSqlLayerPublishingService.BuildLayerGraphId` now incorporates the secure-connection UUID in resource, binding, and publication graph IDs while retaining the legacy IDs for the server-managed store. This intentionally changes newly published external-connection graph IDs; numeric protocol layer identifiers remain unchanged. |
| `SRV-DB-019` | not attempted | Re-verification found that `PersistMetadataV2MutationAsync` still sends one `SaveAsync` call and only handles an unknown commit outcome; it does not reload and rebuild a mutation after `MetadataV2GraphConcurrencyException`. A correct bounded rebase requires restructuring all mutation builders and was deferred so the S1 fix could be completed and tested first. |
| `SRV-DB-024` | not attempted | Re-verification found that `PostgresMetadataV2GraphStore.RefreshSidecarsAsync` still loops over each complete graph collection and executes one insert command per object while the save transaction holds the environment advisory lock. Bulk sidecars and safe snapshot retention require a separate database migration/performance change and were deferred behind the S1 correctness fix. |
