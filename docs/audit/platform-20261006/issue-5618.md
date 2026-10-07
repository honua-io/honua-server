# Issue 5618 audit resolution

Re-verification was performed against the current worktree at `7c422ec`. The highest-severity finding was completed first. The remaining findings were source-traced at their current locations but were not changed in this fix unit, to avoid shallow changes without finding-specific failing tests.

| Finding id | Outcome | Evidence |
|---|---|---|
| SRV-DB-005 | fixed | `SRV_DB_005_PagedCallerOrdering_AppendsPrimaryKeyTiebreaker` in `tests/dotnet/Honua.Db.Postgres.Tests/Features/FeatureStore/PostgresStorageMappedFeatureReaderSqlTests.cs` proves a paged caller sort gains the mapped primary-key order in both inner and outer page queries. `PostgresStorageMappedFeatureReader.BuildOrderExpressions` now appends that key unless the resolved caller ordering already contains it. Observable behavior: ties in paged source-backed queries now have deterministic primary-key ascending order. |
| SRV-DB-016 | not attempted | Re-traced `PostgresFeatureStore.ExecuteFormatQueryAsync`: the distinct/return-distance fallback still constructs `QueryResult` with `hasMoreResults` false after a limited select. No finding-specific failing test was completed after the S1 fix. |
| SRV-DB-022 | not attempted | Re-traced the SQL Server, MySQL, and Oracle query builders: provider paging needs coordinated builder-specific tests before changing ordering. No finding-specific failing test was completed. |
| SRV-GS-007 | not attempted | Re-traced `FeatureServerRequestHandlers.Maintenance.HandleCalculate`: calculate still dispatches internal edit batches through the request context. Atomicity and idempotency require a focused handler test; none was completed. |
| SRV-GS-015 | not attempted | Re-traced estimates, top-features, bins, date-bins, and temporal-extent handlers. A routed-provider test was not completed, so no production change was made. |
| SRV-GS-016 | not attempted | Re-traced `FeatureServerQueryExecutor.SupportsDistinctValues` and its two query-handler consumers; capability remains derived from the executor's default reader. No routed-provider failing test was completed. |
| SRV-GS-017 | not attempted | Re-traced the count-only branches and provider count builders. A distinct-count failing test was not completed, so no provider or advertised-capability behavior was changed. |
| SRV-GS-018 | not attempted | Re-traced both materialized distinct fallbacks in `FeatureServerQueryHandler`; a bounded-scan contract and failing test were not completed. |
| SRV-OGC-019 | not attempted | Re-traced `ODataSearchService.HandleSearchAsync`: `ODataSearchResult` is still returned without a continuation link. No finding-specific paging test was completed. |
| Low: negotiated streaming format | not attempted | Re-traced the streaming query format selection. Deferred until a finding-specific Accept-negotiation test can be added. |
| Low: top-features filter/order | not attempted | Re-traced `FeatureServerRequestHandlers.TopFeatures`; deferred behind the S1/S2 findings without a failing analytics-query test. |
