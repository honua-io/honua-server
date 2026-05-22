# Metadata v2 Cutover Plan

Tracks the in-flight rewrite from v1 metadata (`ILayerCatalog`,
`ServiceDefinition`/`LayerDefinition`, plus the catalog-write
`IServiceMetadataUpdater`/`ILayerMetadataUpdater` interfaces) to the
canonical Metadata v2 graph defined under
`src/Honua.Core/Features/Metadata/Domain/V2/`.

This is a **hard cutover** — no v1→v2 adapter or shim. Every consumer
is rewritten to read the V2 graph directly, then v1 types are deleted
in one sweep. The branch is `feat/metadata-v2-cutover` off `trunk`.

## Done

Commits on `feat/metadata-v2-cutover` (latest first; partial list, see `git log
--oneline cfe776faf..HEAD` for the full set — 24 commits beyond the original plan
commit):

* `952a938eb` fix: tolerate duplicate display names in `MetadataV2GraphIndex` (services published through multiple protocols)
* `70729d924` feat: V2 `BuildPrimaryServiceMapV2` (parallel of v1's BuildPrimaryServiceMap)
* `6abd93c78` refactor: port `TileJsonEndpoints` to V2
* `02318e744` feat: `ServiceProtocols.IsProtocolEnabled` V2 overload
* `527df80bc` feat: V2 overloads on `ServiceDataEditorAuthorization`
* `c98179835` refactor: port `FeatureMutationEventService.ResolveServiceIdAsync` to V2 + protocol-string mapping
* `affe866a5` feat: V2 spatial/temporal extension readers (`ReadSrid`, `ReadBbox`, `ReadGeometryType`, `ReadTemporalFields`, `FindPrimaryGeometryField`, `FindPrimaryIdField`, `MetadataV2ServiceTypeMapping.Map`)
* `0d3e329c1` feat: `ResolvePrimaryServiceV2Async` / `ResolvePrimaryServiceNameV2Async`
* `800d9de12` test: cover V2 layer/collection validation overloads
* `5f3678cc6` feat: V2 validation overloads in `LayerValidationHelpers` (`ValidateLayerWithAccessV2Async`, `ValidateCollectionWithAccessV2Async`, `MetadataV2ValidationResult`)
* `3586c45aa` feat: V2-aware `AccessPolicy` carrier (typed field on `MetadataV2Resource` / `MetadataV2Service`) + helper overloads
* `47d031e3b` refactor: move `AccessPolicy` to `Honua.Core.Features.Security.Domain` (out of `CatalogMetadata.cs`)
* `48cd418d1` fix: port `TileOperationJobServicePublishTests` fixture to V2
* `988c6a338` refactor: delete v1 metadata dead code (`GlobalCapabilities`, `ResourceMetadata`, `FieldSemanticRoles`, projection scaffolds)
* `0cbe9d002` refactor: port `TileOperationJobService` to V2
* `c4aba20a0` refactor: port ImageServer handlers to V2 (8 files)
* `1f5c0f877` refactor: delete v1 `IMetadataProvider` + capabilities formatter scaffolding
* `83e289d51` feat: lean `AdminInfoEndpoints` (`/version` + `/capabilities`) on V2
* `995e7b827` chore: drop v1 manifest/gitops/metadata-resource entries from `EndpointRegistry`
* `5488b2ab3` refactor: delete v1 metadata admin surface (57 files, ~12k LOC)
* `4f932063f` test: register default Metadata v2 graph in `WebAppFixture`
* `46930dce1` refactor: port COG/Zarr/Multidim admin endpoints to V2
* `6267f0cd8` refactor: port `AlertPipeline` to V2
* `cfe776faf` docs: capture metadata v2 cutover plan and remaining work
* `e14edd64e` refactor: port `CacheOperationsEndpoints` to V2
* `a8d6f1449` refactor: port `CacheAdminEndpoints` to V2
* `e6a6c5eb2` test: V2 TestKit fixture + builder
* `7644213a8` test: metadata v2 snapshot + file loader tests
* `ebe93d48e` feat: metadata v2 runtime foundation

Plus partial cluster ports from agent work merged in:

* STAC: `StacV2Lookups`, `StacResourceExtensions`, partial port of `CollectionEndpoints` + `CatalogEndpoints` (2 STAC integration tests remain failing — see "Known gaps" below).
* GeoServices Catalog: `GeoservicesCatalogEndpoints` walks the V2 graph for service-directory enumeration.

Verified green at HEAD:

* `dotnet build` (whole solution): 0 warnings, 0 errors.
* `dotnet test Honua.Core.Tests`: 1680 / 1680 pass.
* `dotnet test Honua.Architecture.Tests`: 63 / 63 pass.
* `dotnet test Honua.Server.Tests` STAC slice: 91 / 93 pass, 2 known failures, 2 environment-skipped.

## Cross-cluster shared-infra V2 surfaces still needed

Parallel agent runs on FeatureServer, MapServer, WMS/WMTS, and OData all stopped
after structural analysis with the same finding: **per-handler ports cannot
complete cleanly until 8 shared helpers gain V2 overloads** (without violating
the no-shims rule):

1. **`IFilterExpressionService.Translate(FilterExpression, MetadataV2Resource)`**
   — used by OData, OGC API Features query adapter, WFS Get/Transaction filter
   pipeline, GeoServices FeatureServer query. Today the only overload takes
   `LayerDefinition`. A V2 overload requires V2 surfaces all the way down
   through `IFilterExpressionTranslator.Translate(FilterExpression,
   MetadataV2Resource)`, `FilterExpressionNormalizer.Normalize(FilterExpression,
   MetadataV2Resource)`, and `ISqlFilterTranslator.Translate(FilterExpression,
   MetadataV2Resource)` — each walks `layer.AttributeFields[].Type` today and
   would walk `resource.SchemaFields` instead. ~4 files, ~400 lines of additions.
2. **`MetadataV2Relationship` + `IRelationshipStore` V2 overload** — OData
   `$metadata`/`$expand`, FeatureServer related-records, WFS join queries all
   need the relationship graph that today lives on
   `LayerDefinition.LayerRelationships`. V2 namespace carries no relationship
   model. Adding a new V2 record + extension reader (`resource.ReadRelationships()`)
   + `IRelationshipStore.GetByResourceId` overload.
3. **`RasterMapRenderingPipeline.RenderRasterTileV2Async(MetadataV2Service,
   IReadOnlyList<(MetadataV2Resource, MetadataV2Publication)>, ...)`** — the
   970-line renderer is consumed by MapServer Tile/Export, WMS GetMap, and WMTS
   GetTile. Without a V2 overload all three protocol clusters remain stuck.
4. **`IQueryParameterAdapter<T>.ConvertAsync(T, MetadataV2Resource, int layerId, ...)`**
   + **`IEditParameterAdapter<T>.ConvertAsync(T, MetadataV2Resource, int layerId, ...)`**
   — Core interfaces re-signed with V2 entities. Used by
   `GeoServicesQueryParameterAdapter`, `ODataQueryParameterAdapter`,
   `Wfs20QueryParameterAdapter`, `OgcFeaturesQueryParameterAdapter`.
5. **`AccessPolicyHelpers.IsLayerAccessible(MetadataV2Resource,
   MetadataV2Service?)`** + **`RequireAnyLayerAccess(IEnumerable<MetadataV2Resource>,
   MetadataV2Service?)`** — V2 overloads exist for
   `RequireResourceAccess`/`IsResourceAccessible`/`RequireAnyResourceAccess` but
   the v1-named overloads still flow `LayerDefinition[]` and `ServiceDefinition`
   through WMS GetCapabilities and FeatureServer access gating.
6. **`OgcClassicRequestHelpers.GetWmsLayerName(MetadataV2Resource)`** — used by
   WMS GetMap/GetFeatureInfo and MapServer Identify to map `<Name>` elements to
   resources.
7. **`IResourceValidator.ValidateServiceAsync(serviceName)` V2 result** — today
   returns `ResourceValidationResult<ServiceDefinition>`. Each protocol
   entry-point dispatches via this. `LayerValidationHelpers.ValidateServiceWithAccessV2Async`
   is now in place (commit `a43bb490e`); per-protocol entry-point dispatchers
   need to migrate onto it.
8. **`TemporalExtentHelpers.TryResolveOptInTemporalFields(MetadataV2Resource)`**
   + **`ResolveTemporalFieldsOrThrow(MetadataV2Resource)`** — companion V2
   overloads for the temporal-resolution surface.
   `TryResolveTemporalRangeV2Async` is in place (commit `a43bb490e`).

These 8 Core/helper additions total roughly **8–12 hours of careful Core work**
and gate the remaining ~50 per-handler ports. Once they land, per-handler ports
go back to the established 1–5 file, &lt;500 line cadence.

## Known gaps

* `StacCollectionsTests.GetCollection_ById_ReturnsDeclaredStacMetadata` and
  `GetCollection_WithProjectedExtent_UsesTransformFallbackForSpatialBbox` —
  both rely on `ILayerMetadataUpdater.UpdateLayerMetadataAsync(layerId,
  CatalogMetadata)` to seed per-test STAC license, keywords, and STAC
  extensions before the request. The V2 STAC port reads from
  `resource.Extensions["stac"]` instead. To fix, replace `UpdateLayerMetadataAsync`
  in the affected tests with a helper that mutates the V2 graph via the
  `TestMetadataV2GraphProvider.SetGraph` hook. Open issue for the test-port slice.

## What was deleted vs ported

The v1 metadata admin surface (k8s-style custom-resource workflows on top of
`MetadataResource`, plus the manifest approval / gitops watch / metadata
compiler infrastructure) had no V2 equivalent: V2 makes the canonical graph
document the editable surface, so admin UX (epic #1046) will edit the
graph via `IMetadataV2GraphStore.SaveAsync`. Since we are pre-release we
deleted it instead of porting it:

* 6 v1 admin endpoint groups + their hosted services and helpers.
* 6 v1 abstractions (`IMetadataResourceStore`, `IMetadataCompiler`,
  `IManifestVersionStore`, `IManifestPendingChangeStore`,
  `IGitOpsWatchStore`, `IMetadataSchemaRegistry`).
* All v1 domain types backing them (`MetadataResource`,
  `MetadataResourceIdentifier`, `MetadataResourceKinds`,
  `MetadataResourceWriteResult`, `CompiledMetadataArtifact`,
  `ManifestVersionEntry`, `ManifestPendingChange`, `GitOpsWatchModels`,
  `MetadataAnnotations`, `MetadataSchemaValidationResult`,
  `MetadataDomainJsonContext`).
* The v1 schema registry (`MetadataSchemaRegistry` +
  `ResourceSchemaDefinition`).
* All four Postgres stores backing the v1 admin workflow.
* Their JSON contexts, request/response models, and admin tests.
* `IMetadataProvider` + `UnifiedMetadataProvider` (the per-request
  enricher) and `ICapabilitiesFormatter<>` — both turned out to be
  scaffolding with no actual consumers in src/ or tests/.

Postgres tables `metadata_resources`, `manifest_versions`,
`manifest_pending_changes`, `gitops_watches` are still defined by
migrations 010/015/016/017 — they're orphaned but harmless and will
get cleaned up by a forward migration when the rest of v1 goes.

Five consumers were ported, not deleted, because they have V2
equivalents that the V2 graph snapshot can serve directly:

| Consumer | v1 → V2 mapping |
|---|---|
| `CacheAdminEndpoints.HandleInvalidateCache` (service scope) | `ILayerCatalog.GetServiceAsync(name).Layers.Select(l => l.Id)` → `snapshot.FindService(name)` + `PublicationsForService(serviceId)` filtered on `LayerIndex` |
| `CacheOperationsEndpoints.HandleInvalidateCache` | same pattern |
| `AlertPipeline.BuildLayerServiceLookupAsync` | `ListServicesAsync().Layers` flatten → walk `snapshot.Services` × `PublicationsForService` |
| `CogEndpoints` / `ZarrEndpoints` / `MultidimensionalCoverageEndpoints` | `ILayerCatalog.LayerExistsAsync(int)` → any publication in snapshot whose `LayerIndex == layerId` |
| `AdminInfoEndpoints` (replaces v1 `/admin/version` and `/admin/capabilities`) | report `MetadataV2Constants.ApiVersion` / `SchemaVersion` directly |

`WebAppFixture` now seeds a `TestMetadataV2GraphProvider` over the
default test seed layer ids (0..5, 101..104) so integration tests
work without a real Postgres V2 snapshot.

## Remaining work, by data type

The v1 surface that still ships is the runtime metadata that protocol
handlers consume per-request: `ILayerCatalog` and the
`ServiceDefinition` / `LayerDefinition` / `ServiceMetadata` /
`LayerMetadata` data shapes it returns. As of HEAD:

| Type | Files still referencing it |
|---|---|
| `LayerDefinition` | ~251 |
| `ILayerCatalog` | ~114 |
| `ServiceDefinition` | ~100 |
| `ServiceMetadata` | ~21 |
| `LayerMetadata` | ~16 |

These are not 251 distinct ports — there is heavy fan-in through
shared helpers (`LayerValidationHelpers`, `AccessPolicyHelpers`,
`OgcFeaturesUtilities`, the `IQueryProcessor` / `IEditProcessor` /
`IFeatureReader` core abstractions) that every protocol handler reads
v1 types through. **The OData and STAC porting attempts both reported
the same blocker: the protocol handlers themselves are largely
V2-clean on their public surface; what binds them to v1 is the
shared infrastructure beneath them.**

Recommended revised cutover order (lower numbers first):

1. **Shared metadata helpers.** Re-sign these on V2 types so the
   protocol handlers above them can be ported one by one without
   shims:
   * `Honua.Server.Features.Infrastructure.Validation.LayerValidationHelpers`
     — every `ValidateLayerWithAccessAsync` / `ValidateODataWriteAccessAsync`
     / `BuildPrimaryServiceMap` caller threads `LayerDefinition` from
     the return type.
   * `Honua.Server.Features.Infrastructure.Authentication.AccessPolicyHelpers`
     — `IsLayerAccessible(LayerDefinition, ServiceDefinition)`.
   * `Honua.Server.Features.Infrastructure.Authentication.ServiceDataEditorAuthorization`
     — `RequireLayerDataEditorAsync` signature.
   * `Honua.Server.Features.Protocols.Ogc.Api.Features.OgcFeaturesUtilities`
     — temporal extent + identifier resolution.
2. **Core processor abstractions.** `IResourceValidator`, `IQueryProcessor`,
   `IEditProcessor`, `IFeatureReader`, `IFeatureWriter`,
   `IFeatureQueryBuilder` all take `LayerDefinition layer` in their
   contracts. Switching them to `MetadataV2Resource` (or to a
   `(snapshot, publication)` pair) cascades into Postgres, DuckDB,
   MySql, SqlServer feature stores.
3. **Protocol handlers**, in order of leaf-most first:
   STAC, OData, OGC API Features, OGC API Maps/Coverages/Tiles/Records,
   GeoServices ImageServer, MapServer, FeatureServer, legacy
   WFS 2.0 / WMS / WMTS / WCS, Terrain, Scene.
4. **`ILayerCatalog` implementations + decorators.** Replace
   `PostgresLayerCatalog` / `DuckDBLayerCatalog` / `MySqlLayerCatalog`
   / `SqlServerLayerCatalog` with the V2 graph store. Delete
   `CachingLayerCatalog`, `BackgroundRefreshCacheDecorator`,
   `MonitoredLayerCatalogDecorator` outright (V2 lookups are already
   O(1) over an immutable snapshot).
5. **Catalog-write paths**: `IServiceMetadataUpdater` /
   `ILayerMetadataUpdater` + `ServiceSettingsEndpoints` /
   `AdminLayerFilterConfigurationEndpoints`. V2 admin writes go through
   `IMetadataV2GraphStore.SaveAsync(updatedGraph)`; consider whether
   we delete these v1 admin write paths in favour of an admin UI on
   top of `IMetadataV2GraphStore`.
6. **Sweep.** Delete `ILayerCatalog`, `ServiceDefinition`,
   `LayerDefinition`, `ServiceMetadata`, `LayerMetadata`,
   `LayerExtrusionMetadata`, `GlobalCapabilities`, `ResourceMetadata`,
   and the v1 catalog domain. Drop migrations 010/015/016/017 +
   their tables in a forward-only cleanup migration.
7. **Tests.** As consumers port, port their tests onto
   `TestMetadataV2GraphBuilder`. When no caller of a v1 fixture
   remains, delete the fixture
   (`TestLayerCatalog`, `TestLayerCatalogWithRelationships`,
   `ODataTestLayerCatalog`, `SpatialReferenceTestLayerCatalog`).

## Open design questions

* **Layer-index identity.** v1 `LayerDefinition.Id` is a per-service
  integer. V2 `Publication.LayerIndex` is `int?`. Esri-style URLs and
  the cache-invalidation surface pin on integers. The test fixture
  always assigns `LayerIndex` for Esri-style publications, but at
  scale we may want a stricter contract.
* **Relationships.** v1 `Relationship` is owned by a layer; V2 has no
  equivalent. The FeatureServer related-records handler is the first
  blocker for this.
* **Capabilities-document projection.** Today every OGC Capabilities /
  GeoServices catalog / STAC catalog / DCAT / ISO output is built
  ad-hoc from `ServiceMetadata` + `LayerMetadata`. The V2 path is:
  `(snapshot, publication, target)` → projection. If duplication
  grows, add a `MetadataV2ProjectionService` (`ProjectionProfile` per
  target) under `src/Honua.Core/Features/Metadata/Projections/V2/`.
* **Test-snapshot seeding contract.** `WebAppFixture` now seeds a V2
  snapshot statically by layer-id range. If `tests/seed/server.yaml`
  is regenerated with different ids, the seed in `WebAppFixture` needs
  to be regenerated too. Long-term: derive the snapshot directly from
  the YAML seed.
