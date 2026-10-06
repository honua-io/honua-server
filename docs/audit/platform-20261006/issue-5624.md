# Issue 5624 audit disposition

Re-verification was performed against the current worktree on 2026-10-06. The
unit contains several independent authorization changes; this pass completed
the directly exploitable temporary-object-key finding and records the remaining
verified findings for follow-up rather than changing them without focused
regression tests.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-INF-001` | not attempted | Re-verified: `PMTilesProxyEndpoints.HandleProxy` still resolves and streams the artifact without resolving its source resource or calling `AccessPolicyHelpers` (`src/Honua.Server/Features/Protocols/Tiles/PMTilesProxy/PMTilesProxyEndpoints.cs`). |
| `SRV-OGC-001` | not attempted | Re-verified: the no-collections branch still calls tenant-blind `AccessPolicyHelpers.EvaluateAccess` (`src/Honua.Protocols.OgcApi/Maps/Handlers/OgcMapsRenderingHandler.cs`). |
| `SRV-AUTH-003` | fixed | `SRV_AUTH_003_RejectsEncodedTemporaryObjectKeyWithParentTraversal` proves a client-encoded parent traversal is rejected before any storage-provider lookup. `CloudBackedTemporaryFileService.TryDecodeCloudObjectKey` now accepts only a direct child of `temporary-files`, rejecting separators, `..`, and percent-encoded input. |
| `SRV-AUTH-009` | not attempted | Re-verified: `PostgresStudioPackageStore.GetActivePublicationRequestByRouteAsync` still selects the newest accepted route claim without an owner or tenant predicate (`src/Honua.Db/Postgres/Features/Studio/PostgresStudioPackageStore.cs`). |
| `SRV-OGC-008` | not attempted | Re-verified: `OgcStylesEndpoints.AuthorizeStyleAsync` still resolves resource names with `OrdinalIgnoreCase`, while the projection lookup remains ordinal (`src/Honua.Protocols.OgcApi/Styles/OgcStylesEndpoints.cs`). |
| `SRV-OGC-012` | not attempted | Re-verified: `HandleCreateStoredQuery` and `HandleDropStoredQuery` still gate management only on `Identity.IsAuthenticated` (`src/Honua.Protocols.OgcClassic/Wfs20/Wfs20DispatcherEndpoint.cs`). |
| `SRV-OGC-013` | not attempted | Re-verified: `ElevationAccessGuard.EnforceReadAccessAsync` still uses protocol-preferred resolution without a strict `MetadataV2ServiceProtocols.IsProtocolEnabled` check (`src/Honua.Scene/Grpc/ElevationAccessGuard.cs`). |
| `SRV-OGC-020` | not attempted | Re-verified: map rendering and map tileset handlers still use synchronous coarse-policy `RequireResourceAccess` calls (`src/Honua.Protocols.OgcApi/Maps/Handlers/OgcMapsRenderingHandler.cs`, `src/Honua.Protocols.OgcApi/Maps/Handlers/OgcMapsTileSetHandler.cs`). |
| Imported tile-cache serving routes | not attempted | Re-verified: the `/tiles/imported` serving group still has no resource binding or tenant-aware authorization (`src/Honua.Import/Features/TileCachePackage/TileCachePackageEndpoints.cs`). |

## Observable behavior

Malformed stateless temporary-file tokens that decode to nested, traversal, or
percent-encoded object keys now return the existing not-found response without
contacting cloud storage. Server-issued direct temporary-file keys and their
public URL format remain compatible.
