# GeoServices REST Parity

This is the canonical GeoServices REST landing page for Honua Server.
Start here, then use the linked drill-down pages for endpoint-level parameters, limitations, and implementation evidence.

Published artifacts:
- Human-readable landing page: this page
- Service drill-down pages: [FeatureServer](feature-server-matrix.md), [MapServer](map-server-matrix.md), [ImageServer](image-server-matrix.md), [Geometry Service](geometry-service-matrix.md)
- Machine-readable parity export: [data/geoservices-rest-parity.json](data/geoservices-rest-parity.json)

## Status vocabulary

- Implemented: the Esri operation/resource exists in Honua at a compatible path and the documented behavior is supported.
- Partial: the Esri operation/resource exists, but Honua only supports a subset of the documented parameters or behavior.
- Not implemented: the Esri operation/resource is not exposed by Honua.

## Service summary

| Service | Honua parity | Implemented surface | Explicit gaps called out here | Drill-down |
| --- | --- | --- | --- | --- |
| FeatureServer | Supported with partial parity | Query, edits, attachments, related records, domains, replication, estimates, calculate, validate SQL, append, bins/date bins/top features, spatial analytics extensions (Pro) | Cleanup change tracking, contingent values, shared templates, asset/3D operations, broader ArcGIS SQL parity | [FeatureServer Matrix](feature-server-matrix.md) |
| MapServer | Supported with partial parity | Service/layer metadata, export, identify, find, legend, query, tiles, generateKml, WMS, WMTS | Export tiles, estimate export tile size, generate renderer, query attachments/related records/domains/legends, several Esri child resources | [MapServer Matrix](map-server-matrix.md) |
| ImageServer | Supported with partial parity | Service metadata (with `timeInfo`), exportImage, identify, tile, raster catalog `query`, `computeStatisticsHistograms`, `legend`, `computeClass` raster-function chain validation | Catalog mutation, export tiles, find/queryBoundary, measure/project, AOI clipping for stats/histograms, full raster catalog child resources, WMTS, richer metadata resources | [ImageServer Matrix](image-server-matrix.md) |
| Geometry Service | Supported with partial parity | Buffer, simplify, project, intersect, union, clip, difference, supplemental `area` and `length` routes | Root metadata resource, most ArcGIS Geometry Service operations, canonical `areasAndLengths`/`lengths` routes | [Geometry Service Matrix](geometry-service-matrix.md) |
| GPServer | Supported with partial parity | PrintingTools GPServer surface: task metadata, sync execute, async submitJob, job status, job result (Export Web Map Task, Get Layout Templates Info Task) | Generic GP adapter for arbitrary services: task discovery, cancel job, multi-parameter results. Canonical model mapped in #360; adapter is #723. | [Geoprocess Framework Analysis](geoprocess-framework-analysis.md) |

## Evidence map

| Service | Endpoint mapping | Primary tests |
| --- | --- | --- |
| GPServer | [PrintingTools endpoints](../../src/Honua.Server/Features/PrintingTools/PrintingToolsEndpoints.cs), canonical model: [geoprocess framework analysis](geoprocess-framework-analysis.md), adapter mapping: [ADR-0029](../contributor/adr/0029-geoprocess-canonical-model-mappings.md) | [PrintingTools tests](../../tests/Honua.Server.Tests/Features/PrintingTools/PrintingToolsEndpointTests.cs), [gRPC process service](../../tests/Honua.Server.Tests/Features/Geoprocessing/GrpcProcessServiceTests.cs), [integration](../../tests/Honua.Server.Tests/Features/Geoprocessing/GrpcProcessServiceIntegrationTests.cs) |
| FeatureServer | [FeatureServer endpoints](../../src/Honua.Server/Features/FeatureServer/FeatureServerEndpoints.cs), [query handlers](../../src/Honua.Server/Features/FeatureServer/FeatureServerRequestHandlers.Query.cs), [edit handlers](../../src/Honua.Server/Features/FeatureServer/FeatureServerRequestHandlers.Edits.cs), [replication handlers](../../src/Honua.Server/Features/FeatureServer/FeatureServerRequestHandlers.Replication.cs), [attachment endpoints](../../src/Honua.Server/Features/FeatureServer/AttachmentEndpoints.cs), [spatial analytics endpoints](../../src/Honua.Server/Features/SpatialAnalytics/SpatialAnalyticsEndpoints.cs) | [query parameters](../../tests/Honua.Server.Tests/Features/FeatureServer/FeatureServerQueryParameterTests.cs), [replication](../../tests/Honua.Server.Tests/Features/FeatureServer/FeatureServerReplicationTests.cs), [maintenance](../../tests/Honua.Server.Tests/Features/FeatureServer/FeatureServerMaintenanceTests.cs), [spatial analytics REST](../../tests/Honua.Server.Tests/Features/SpatialAnalytics/SpatialAnalyticsRestTests.cs), [spatial analytics reader availability](../../tests/Honua.Server.Tests/Features/SpatialAnalytics/SpatialAnalyticsReaderAvailabilityTests.cs) |
| MapServer | [MapServer endpoints](../../src/Honua.Server/Features/MapServer/MapServerEndpoints.cs), [export handler](../../src/Honua.Server/Features/MapServer/MapServerRequestHandlers.Export.cs), [identify handler](../../src/Honua.Server/Features/MapServer/MapServerRequestHandlers.Identify.cs), [WMTS handler](../../src/Honua.Server/Features/MapServer/MapServerRequestHandlers.Wmts.cs), [WMS handler](../../src/Honua.Server/Features/MapServer/MapServerRequestHandlers.Wms.cs) | [endpoint coverage](../../tests/Honua.Server.Tests/Features/MapServer/MapServerEndpointTests.cs), [tiles](../../tests/Honua.Server.Tests/Features/MapServer/MapServerTileEndpointTests.cs), [WMS](../../tests/Honua.Server.Tests/Features/MapServer/MapServerWmsTests.cs), [WMTS](../../tests/Honua.Server.Tests/Features/MapServer/MapServerWmtsTests.cs) |
| ImageServer | [ImageServer endpoints](../../src/Honua.Server/Features/ImageServer/ImageServerEndpoints.cs), [metadata handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerMetadataHandler.cs), [export handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerExportHandler.cs), [identify handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerIdentifyHandler.cs), [tile handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerTileHandler.cs), [catalog query handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerCatalogQueryHandler.cs), [statistics/histograms handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerStatisticsHistogramsHandler.cs), [legend handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerLegendHandler.cs), [analyze handler](../../src/Honua.Server/Features/ImageServer/Handlers/ImageServerAnalyzeHandler.cs) | [basic coverage](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerBasicTests.cs), [parameter validation](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerParameterValidationTests.cs), [error handling](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerErrorHandlingTests.cs), [endpoint coverage](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerEndpointsTests.cs), [catalog query](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerCatalogQueryHandlerTests.cs), [statistics/histograms](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerStatisticsHistogramsHandlerTests.cs), [legend](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerLegendHandlerTests.cs), [analyze](../../tests/Honua.Server.Tests/Features/ImageServer/ImageServerAnalyzeHandlerTests.cs) |
| Geometry Service | [geometry endpoints](../../src/Honua.Server/Features/GeometryService/GeometryServiceEndpoints.cs), [request parser](../../src/Honua.Server/Features/GeometryService/Services/GeometryServiceRequestParser.cs), [operation handler](../../src/Honua.Server/Features/GeometryService/Services/GeometryServiceHandler.cs) | [buffer](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceBufferTests.cs), [project](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceProjectTests.cs), [simplify](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceSimplifyTests.cs), [advanced operations](../../tests/Honua.Server.Tests/Features/GeometryService/GeometryServiceAdvancedOperationsTests.cs) |

## Source specifications

- [Esri Feature Service](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/)
- [Esri Layer (Feature Service)](https://developers.arcgis.com/rest/services-reference/enterprise/layer-feature-service/)
- [Esri Map Service](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/)
- [Esri Layer / Table (Map Service)](https://developers.arcgis.com/rest/services-reference/enterprise/layer-table/)
- [Esri Image Service](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/)
- [Esri Geometry Service](https://developers.arcgis.com/rest/services-reference/enterprise/geometry-service/)
- [Esri GP Service](https://developers.arcgis.com/rest/services-reference/enterprise/gp-service/)
- [Esri GP Task](https://developers.arcgis.com/rest/services-reference/enterprise/gp-task/)

## Upkeep

- When GeoServices routes, parameter handling, or response shapes change, update the relevant drill-down page and [data/geoservices-rest-parity.json](data/geoservices-rest-parity.json) in the same PR.
- Release owners verify these parity docs during the [Release Checklist](../contributor/RELEASE_CHECKLIST.md#compatibility-contract-updates-required).
