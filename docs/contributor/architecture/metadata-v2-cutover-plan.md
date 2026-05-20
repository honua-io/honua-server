# Metadata v2 Cutover Plan

Tracks the in-flight rewrite from v1 metadata (`ILayerCatalog`,
`IMetadataProvider`, `ServiceDefinition`/`LayerDefinition`,
`IMetadataResourceStore`, `IMetadataSchemaRegistry`, `ServiceMetadata` /
`LayerMetadata`, and friends) to the canonical Metadata v2 graph defined
under `src/Honua.Core/Features/Metadata/Domain/V2/`.

This is a **hard cutover** — no v1→v2 adapter or shim. Every consumer is
rewritten to read the V2 graph directly, then v1 types are deleted in one
sweep. The branch is `feat/metadata-v2-cutover` off `trunk`.

## Done

Commits on `feat/metadata-v2-cutover`:

| # | Commit | Scope |
|---|--------|-------|
| 1 | `ebe93d48e` | V2 runtime foundation (`IMetadataV2GraphProvider` / `Store`, `MetadataV2GraphSnapshot` + indexes + extensions, file loader, `PostgresMetadataV2GraphStore` + sidecar tables, migration `031_CreateMetadataV2Snapshot.sql`, DI wiring) |
| 2 | `7644213a8` | 10 unit tests covering snapshot indexes, multi-hop accessors, file loader, validation, missing-file behaviour |
| 3 | `e6a6c5eb2` | `TestMetadataV2GraphProvider` + `TestMetadataV2GraphBuilder` (TestKit leaf fixture) |
| 4 | `a8d6f1449` | `CacheAdminEndpoints` ported to V2 (`ResolveServiceLayerIds` from publications) |
| 5 | `e14edd64e` | `CacheOperationsEndpoints` ported to V2 |

All commits are green at every step (`dotnet build` and `dotnet test`
both pass; foundation tests verified at commit #2).

## Remaining work, by layer

Counts taken at commit `e14edd64e`. They drop as consumers are ported.

### Consumers still touching v1 metadata (~110 files)

| Cluster | Files | Notes |
|---|---|---|
| Catalog / FeatureStore core | ~13 | `ILayerCatalog` is consumed by `FeatureProviderBindingResolver`, `FeatureProviderQueryRouter`, `PostgresLayerCatalog`, `DuckDBLayerCatalog`, `MySqlLayerCatalog`. These are the runtime roots — port last so dependents have already moved. |
| GeoServices REST | ~18 | `FeatureServer*`, `MapServer*`, `ImageServer*` handlers. Many use `ServiceMetadata`/`LayerMetadata` deeply (capabilities docs, queries, related-records). Port the lightest ImageServer handlers (Analyze, Legend, Tile) first to validate the pattern. |
| OGC APIs | ~20 | OGC API Features (`CollectionsEndpoints`, `OgcFeaturesQueryHandler`), Maps (`RenderingHandler`, `TileSetHandler`), Coverages, Tiles, Records, plus legacy WFS 2.0 / WMS / WMTS / WCS. Capabilities-doc-heavy. |
| STAC / DCAT / OData / OGC Records | ~13 | Currently project from `CatalogMetadataSemantics` (v1). Will project from V2 resources via `MetadataV2GraphSnapshotExtensions` + `ProjectionProfiles`. |
| Admin / HTTP controllers | ~10 | `AdminManifestApprovalEndpoints`, `AdminManifestDriftEndpoints`, `AdminMetadataEndpoints`, `GitOpsWatchService`, `ManifestDriftWebhookDispatcher`, `MetadataResourceEndpoints`, `MetadataCompilerService`, `ServiceSettingsEndpoints`. |
| Infrastructure decorators | ~6 | `CachingLayerCatalog`, `BackgroundRefreshCacheDecorator`, `MonitoredLayerCatalogDecorator`, `OutputCacheInvalidationService`, `LayerValidationService`. Decorators get **deleted**, not ported — V2 lookups are already O(1) over an immutable snapshot, so decorating buys nothing. |
| Process / rendering / sync | ~18 | `TileOperationJobService`, `RasterMapRenderingPipeline`, `SceneTilesPublishExecutor`, `GroundingService`, validation helpers, static map, spatial analytics, Zarr. |

### Tests still bound to v1 fixtures (~64 files)

* TestKit fixtures used as starting v1 surface: `TestLayerCatalog`, `TestLayerCatalogWithRelationships`, `ODataTestLayerCatalog`, `SpatialReferenceTestLayerCatalog`, `WebAppFixture`'s default `ILayerCatalog` registration.
* Consumer-specific tests inherit from those fixtures.
* Strategy: replace each consumer's test fixture with `TestMetadataV2GraphBuilder` (introduced in commit `e6a6c5eb2`) as part of the same commit that ports the consumer. When no caller of a v1 fixture remains, delete the fixture.

### v1 surface to delete (~35 files)

`src/Honua.Core/Features/Metadata/`:

* `Abstractions/`: `ICapabilitiesFormatter.cs`, `IGitOpsWatchStore.cs`, `IManifestPendingChangeStore.cs`, `IManifestVersionStore.cs`, `IMetadataCompiler.cs`, `IMetadataProvider.cs`, `IMetadataResourceStore.cs`, `IMetadataSchemaRegistry.cs`
* `Domain/`: `CompiledMetadataArtifact.cs`, `GitOpsWatchModels.cs`, `GlobalCapabilities.cs`, `LayerExtrusionMetadata.cs`, `LayerMetadata.cs`, `ManifestPendingChange.cs`, `ManifestVersionModels.cs`, `MetadataAnnotations.cs`, `MetadataDomainJsonContext.cs`, `MetadataResource.cs`, `MetadataResourceIdentifier.cs`, `MetadataResourceKinds.cs`, `MetadataResourceWriteResult.cs`, `MetadataSchemaValidationResult.cs`, `ResourceMetadata.cs`, `ServiceMetadata.cs`
* `Schema/`: `MetadataSchemaRegistry.cs`, `ResourceSchemaDefinition.cs`
* `Services/`: `UnifiedMetadataProvider.cs`, `UnifiedMetadataProviderLog.cs`
* `Projections/`: `CatalogMetadataSemantics.cs`, `FieldSemanticRoles.cs`, `ProjectionReadinessEvaluator.cs`, `ProjectionTargets.cs`
* `MetadataServiceCollectionExtensions.cs`: remove `AddUnifiedMetadata`

`src/Honua.Postgres/Features/Metadata/`:

* `PostgresMetadataResourceStore.cs`, `PostgresManifestPendingChangeStore.cs`, `PostgresManifestVersionStore.cs`, `PostgresGitOpsWatchStore.cs`

`src/Honua.Core/Features/Catalog/Abstractions/ILayerCatalog.cs` and the
v1 `ServiceDefinition` / `LayerDefinition` / `Relationship` / catalog
domain types — deletion is gated on all FeatureStore + GeoServices
handlers being ported.

## Execution order

Bottom-up so the build is green commit-to-commit:

1. **Done — Foundation:** V2 graph runtime + Postgres + DI + TestKit fixture.
2. **In progress — Admin/cache (2 of ~3 done):** `CacheAdminEndpoints`, `CacheOperationsEndpoints`; remaining: `AdminMetadataEndpoints`, `AdminManifestApprovalEndpoints`, `AdminManifestDriftEndpoints`, `MetadataResourceEndpoints`, `ServiceSettingsEndpoints`, `MetadataCompilerService`, `GitOpsWatchService`.
3. **Next — Light protocol consumers:** `Stac/CollectionEndpoints`, `Stac/CatalogEndpoints`, `Cog/CogEndpoints`, `Zarr/ZarrEndpoints`, `Coverages/Multidimensional/...`, `OData/ODataCrudHandler` and `OData/ODataServiceCollectionExtensions`, `ImageServer/Handlers/*` (~18).
4. **GeoServices Feature/Map server family:** ~12 handlers each. Need V2-aware capabilities-doc generation, query routing, relationship resolution.
5. **OGC API family:** `OgcFeaturesQueryHandler`, `CollectionsEndpoints`, Maps/Coverages/Tiles/Records — these all build capability docs that today come from `ServiceMetadata`/`LayerMetadata`. Switch to building from V2 snapshot + publication.
6. **WFS/WMS/WMTS/WCS legacy handlers:** capabilities docs again, plus transaction context. ~3 files.
7. **Process + rendering + sync:** `TileOperationJobService`, `RasterMapRenderingPipeline`, `SceneTilesPublishExecutor`, `LayerValidationService`, `GroundingService`, static map, spatial analytics, Zarr.
8. **FeatureStore + provider routing:** `FeatureProviderBindingResolver`, `FeatureProviderQueryRouter`. They map "layer id → storage connection." V2 equivalent: "publication id → storage binding → connection."
9. **`ILayerCatalog` implementations:** delete `PostgresLayerCatalog`, `DuckDBLayerCatalog`, `MySqlLayerCatalog`, plus their decorators (`CachingLayerCatalog`, `BackgroundRefreshCacheDecorator`, `MonitoredLayerCatalogDecorator`). Replace per-provider DI registration with the V2 graph store registration.
10. **Tests:** as each consumer ports, port its tests to `TestMetadataV2GraphBuilder`. The TestKit fixtures (`TestLayerCatalog*`, `ODataTestLayerCatalog`, `SpatialReferenceTestLayerCatalog`) are deleted only after no caller remains.
11. **Sweep:** delete all 35 v1 files above, run `dotnet test`, fix any straggling references.

## Open design questions

* **Layer index identity.** v1 `LayerDefinition.Id` is a per-service integer. V2 `Publication.LayerIndex` is `int?` (nullable). Existing tests and external clients (Esri-style URLs) pin on integer layer ids. The fixture/builder must always assign `LayerIndex` for Esri-style publications.
* **Relationships.** v1 `Relationship` lives on the layer; V2 has nothing equivalent yet. Need to model relationships in the V2 graph (either as a top-level entity referenced from publications, or via `extensions` on the resource). Blocks the FeatureServer related-records handler port.
* **Capabilities documents.** v1 capabilities (OGC Capabilities XML, GeoServices catalog JSON, STAC catalog JSON, DCAT RDF, ISO 19115 XML) are built from `ServiceMetadata`+`LayerMetadata`. The V2 path is: read snapshot + filter publications by service + project to target format. The projection logic is currently spread across many handler files; consider a `MetadataV2ProjectionService` if duplication grows.
* **Test database for V2 snapshot.** Server.Tests `WebAppFixture` runs a real Postgres testcontainer. The V2 `PostgresMetadataV2GraphStore` requires a snapshot to be present before any `GetCurrentAsync` call. Either seed a fixture snapshot in `WebAppFixture`, or register `TestMetadataV2GraphProvider` in test DI overriding the Postgres provider for tests that don't need the DB path.
