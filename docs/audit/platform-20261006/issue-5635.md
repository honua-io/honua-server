# Issue 5635 audit disposition

Current-code re-verification and remediation record for audit `platform-20261006`, group `G-SRV-23`.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-DB-013` | fixed | `SRV_DB_013_Truncate_DoesNotSplitSurrogatePairAtBoundary` proves truncation backs up before a UTF-16 surrogate pair. `PostgresAuditLog.Truncate` now preserves a valid string before appending the marker. |
| `SRV-AUTH-002` | not attempted | Re-verified in `src/Honua.Server/Program.cs`: `IConsoleContentStore`, `IConsoleShareStore`, and `IConsoleOpenDataStore` are still registered as process-local in-memory stores. Durable replacements require a larger persistence design and migration than this fix unit could safely complete. |
| `SRV-INF-009` | not attempted | Re-verified in `src/Honua.Server/Features/Infrastructure/RateLimiting/RateLimitingMiddleware.cs`: `CheckRateLimitMemory` still rejects a new key when 10,000 live entries remain after pruning. No safe eviction/concurrency regression test was completed in this fix unit. |
| `SRV-INF-010` | fixed | `SRV_INF_010_ControlPlaneMutation_ReturnsAuditDescriptor` covers Console content/share and enrichment mutations. `DefaultAuditActionResolver` now classifies mutating routes under both control-plane prefixes as admin actions. |
| `SRV-INF-011` | fixed | `SRV_INF_011_CredentialedRequest_IsPrivateAndVariesByCredentials` proves every principal-selecting credential header (Authorization, X-API-Key, X-Esri-Authorization, X-Honua-Embed-Key, X-Honua-Token) yields `private` caching plus a `Vary` over all of them and `Cookie`; `SRV_INF_011_AnonymousRequest_IsPublicWithoutCredentialVary` keeps anonymous behavior public. |
| `SRV-INF-013` | fixed | `SRV_INF_013_CancellingLeadingCaller_DoesNotCancelSharedLoad` proves the shared load uses an independent token while each caller can cancel only its own wait; `SRV_INF_013_SharedLoad_RunsInOwnScope_SurvivingInitiatingRequestScope` proves the load resolves the store from a load-owned DI scope, so disposing the initiating request's scope cannot tear down its connection provider. |
| `SRV-INF-014` | fixed | `SRV_INF_014_UnknownPathSegment_UsesBoundedOperationLabel` proves an arbitrary path segment maps to `unknown`; `ResolveOperation` now admits only a closed set of GeoServices operation terminals; `SRV_INF_014_SupportedOperationTerminal_KeepsOperationLabel` proves supported terminals such as `queryTopFeatures`, `validateSQL`, and `createReplica` keep their labels. |
| `SRV-INF-018` | fixed | `SRV_INF_018_UnknownRequestValue_HasBoundedOperationLabel` covers WFS, WMS, WMTS, and WCS. Known request names retain their existing labels and all other values map to `<protocol>.unsupported`. |
| `LOW-AdminLayerStyleEndpoints-cache` | not attempted | Re-verified in `src/Honua.Server/Features/Admin/AdminLayerStyleEndpoints.cs`; layer invalidation still does not explicitly demonstrate eviction of the `ogc-styles` tag. Deferred because the S2 findings took priority. |
| `LOW-ServiceSettingsEndpoints-raster-mosaic` | not attempted | Re-verified in `src/Honua.Server/Features/Admin/ServiceSettingsEndpoints.cs`; the raster mosaic merge strategy still lacks a Metadata v2 persistence representation. Deferred because the S2 findings took priority. |
| `LOW-ConsoleShareMapper-visibility` | not attempted | Re-verified in `src/Honua.Server/Features/Console/ConsoleShareMapper.cs`; explicit share state still takes precedence over content visibility. Deferred because the S2 findings took priority. |
| `LOW-SecureConnectionEndpoints-draft-test` | not attempted | Re-verified in `src/Honua.Server/Features/Admin/SecureConnectionEndpoints.cs`; the draft test path resolves its secret separately from the persisted connection resolver. Deferred because the S2 findings took priority. |
| `LOW-PostgresUserStore-null-ranking` | not attempted | Re-verified in `src/Honua.Db/Postgres/Features/Identity/PostgresUserStore.cs`; the issuer-less ranking expression does not specify null-safe ordering. Deferred because the S2 findings took priority. |
| `LOW-Extensions-trace-path-redaction` | not attempted | Re-verified in `src/Honua.ServiceDefaults/Extensions.cs`; span sanitization does not apply credential-route path redaction. Deferred because the S2 findings took priority. |
| `LOW-TileJsonEndpoints-storage-layer` | not attempted | Re-verified in `src/Honua.Server/Features/Protocols/Tiles/TileJsonEndpoints.cs`; extent lookup still uses the publication layer index. Deferred because the S2 findings took priority. |

## Observable behavior

- Credentialed terrain metadata and tile responses change from shared-cacheable `public` responses to `private` responses and vary on credential headers.
- Unknown error-path and classic OGC `REQUEST` values now collapse to bounded telemetry labels.
- Console/enrichment control-plane mutations now emit the same middleware audit category as other administrative mutations.
- Cancelling one metadata-cache waiter no longer cancels the shared snapshot load for other requests; the load runs in its own DI scope rather than the initiating request's.
