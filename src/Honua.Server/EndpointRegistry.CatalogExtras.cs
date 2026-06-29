// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> CatalogExtraEndpoints =>
    [
        // Data export
        new("GET", "/api/v1/admin/services/{serviceName}/layers/{layerId}/export"),

        // COG admin endpoints (#519)
        new("POST", "/api/v1/admin/cloud-rasters"),
        new("GET", "/api/v1/admin/cloud-rasters"),
        new("GET", "/api/v1/admin/cloud-rasters/{id}"),
        new("DELETE", "/api/v1/admin/cloud-rasters/{id}"),
        new("POST", "/api/v1/admin/cloud-rasters/{id}/refresh"),

        // Zarr admin endpoints (#1009)
        new("POST", "/api/v1/admin/zarr-stores"),
        new("GET", "/api/v1/admin/zarr-stores"),
        new("GET", "/api/v1/admin/zarr-stores/{id}"),
        new("DELETE", "/api/v1/admin/zarr-stores/{id}"),
        new("POST", "/api/v1/admin/zarr-stores/{id}/refresh"),

        // Datacube tile rendering (#1835): Zarr coverage slice -> PNG map tile
        new("GET", "/api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}"),

        // STAC (SpatioTemporal Asset Catalog)
        new("GET", "/stac"),
        new("GET", "/stac/conformance"),
        new("GET", "/stac/openapi.json"),
        new("GET", "/stac/collections"),
        new("GET", "/stac/collections/{collectionId}"),
        new("GET", "/stac/collections/{collectionId}/items"),
        new("GET", "/stac/collections/{collectionId}/items/{itemId}"),
        new("GET", "/stac/queryables"),
        new("GET", "/stac/collections/{collectionId}/queryables"),
        new("GET", "/stac/search"),
        new("POST", "/stac/search"),

        // OGC SensorThings API (STA v1.1) read surface (#1747)
        new("GET", "/sta/v1.1/Things"),
        new("GET", "/sta/v1.1/Things({id})"),
        new("GET", "/sta/v1.1/Sensors"),
        new("GET", "/sta/v1.1/Sensors({id})"),
        new("GET", "/sta/v1.1/ObservedProperties"),
        new("GET", "/sta/v1.1/ObservedProperties({id})"),
        new("GET", "/sta/v1.1/Datastreams"),
        new("GET", "/sta/v1.1/Datastreams({id})"),
        new("GET", "/sta/v1.1/Datastreams({id})/Observations"),
        new("GET", "/sta/v1.1/Observations"),
        new("GET", "/sta/v1.1/Observations({id})"),

        // OGC SensorThings API (STA v1.1) Phase 2 ingest + Phase 3 streaming (#1747)
        new("POST", "/sta/v1.1/Observations"),
        new("POST", "/sta/v1.1/Datastreams({id})/Observations"),
        new("POST", "/sta/v1.1/Datastreams"),
        new("GET", "/sta/v1.1/ObservationsStream"),

        // Hosted samples
        new("GET", "/samples/stac-ops"),

        // MCP operator surface (#728) — JSON-RPC dispatch over a single route.
        // Streamable-HTTP transport (#1954) adds the SSE GET stream and the
        // session-termination DELETE alongside the POST dispatcher.
        new("POST", "/v1/grounding/spec/mutate"),
        new("POST", "/v1/grounding/spec/summarize"),
        new("POST", "/mcp"),
        new("GET", "/mcp"),
        new("DELETE", "/mcp"),

        // Spec plan / apply engine (#789).
        new("POST", "/v1/spec/validate"),
        new("POST", "/v1/spec/plan"),
        new("POST", "/v1/spec/apply"),
        new("POST", "/v1/spec/cancel"),
        new("GET", "/v1/spec/artifact/{hash}"),

        // Analysis content HTTP surface (#1182, #1237).
        // NL-assisted analysis-package and saved-query generation.
        new("POST", "/api/v1/analysis/content/generate"),
        new("POST", "/api/v1/analysis/content/queries/generate"),
        new("POST", "/api/v1/analysis/content/items"),
        new("GET", "/api/v1/analysis/content/items"),
        new("GET", "/api/v1/analysis/content/items/{itemId}"),
        new("GET", "/api/v1/analysis/content/items/{itemId}/versions/latest"),
        new("GET", "/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}"),
        new("POST", "/api/v1/analysis/content/items/{itemId}/versions"),
        new("POST", "/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/estimate"),
        new("POST", "/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/preview"),
        new("POST", "/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/runs"),
        new("POST", "/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}/reruns"),
        new("GET", "/api/v1/analysis/artifacts/{artifactId}"),
        new("GET", "/api/v1/analysis/jobs/{jobId}/logs"),
        new("GET", "/api/v1/analysis/jobs/{jobId}/failure"),

        // Analysis report HTTP surface (#801).
        new("GET", "/api/v1/analysis/reports/{jobId}"),
        new("GET", "/api/v1/analysis/reports/{jobId}/render"),

        // Cloud-optimized HDF5 / NetCDF4 multidimensional coverage admin endpoints (#1010).
        new("POST", "/api/v1/admin/multidim-coverages"),
        new("GET", "/api/v1/admin/multidim-coverages"),
        new("GET", "/api/v1/admin/multidim-coverages/{id}"),
        new("DELETE", "/api/v1/admin/multidim-coverages/{id}"),
        new("POST", "/api/v1/admin/multidim-coverages/{id}/refresh"),
        new("GET", "/api/v1/admin/multidim-coverages/jobs/{jobId}"),

        // Federated-query source configuration and query-plan inspection (#341).
        new("GET", "/api/v1/admin/federation/sources"),
        new("GET", "/api/v1/admin/federation/sources/{id}/plan"),
    ];
}
