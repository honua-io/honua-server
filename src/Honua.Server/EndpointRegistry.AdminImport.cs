// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> AdminImportEndpoints =>
    [
        // v1 admin import endpoints (primary)
        new("GET", "/api/v1/admin/import/formats"),
        new("POST", "/api/v1/admin/import/formats"),
        new("PUT", "/api/v1/admin/import/formats"),
        new("DELETE", "/api/v1/admin/import/formats"),
        new("PATCH", "/api/v1/admin/import/formats"),
        new("POST", "/api/v1/admin/import/preview"),
        new("GET", "/api/v1/admin/import/preview"),
        new("PUT", "/api/v1/admin/import/preview"),
        new("DELETE", "/api/v1/admin/import/preview"),
        new("PATCH", "/api/v1/admin/import/preview"),
        new("POST", "/api/v1/admin/import/preview-url"),
        new("GET", "/api/v1/admin/import/preview-url"),
        new("PUT", "/api/v1/admin/import/preview-url"),
        new("DELETE", "/api/v1/admin/import/preview-url"),
        new("PATCH", "/api/v1/admin/import/preview-url"),
        new("POST", "/api/v1/admin/import/upload"),
        new("GET", "/api/v1/admin/import/upload"),
        new("PUT", "/api/v1/admin/import/upload"),
        new("DELETE", "/api/v1/admin/import/upload"),
        new("PATCH", "/api/v1/admin/import/upload"),
        new("POST", "/api/v1/admin/import/upload-url"),
        new("GET", "/api/v1/admin/import/upload-url"),
        new("PUT", "/api/v1/admin/import/upload-url"),
        new("DELETE", "/api/v1/admin/import/upload-url"),
        new("PATCH", "/api/v1/admin/import/upload-url"),
        new("GET", "/api/v1/admin/import/uploads/{uploadId}/progress"),
        new("POST", "/api/v1/admin/import/uploads/{uploadId}/progress"),
        new("PUT", "/api/v1/admin/import/uploads/{uploadId}/progress"),
        new("DELETE", "/api/v1/admin/import/uploads/{uploadId}/progress"),
        new("PATCH", "/api/v1/admin/import/uploads/{uploadId}/progress"),
        new("POST", "/api/v1/admin/import/uploads/{uploadId}/cancel"),
        new("GET", "/api/v1/admin/import/uploads/{uploadId}/cancel"),
        new("PUT", "/api/v1/admin/import/uploads/{uploadId}/cancel"),
        new("DELETE", "/api/v1/admin/import/uploads/{uploadId}/cancel"),
        new("PATCH", "/api/v1/admin/import/uploads/{uploadId}/cancel"),
        new("GET", "/api/v1/admin/import/uploads"),
        new("POST", "/api/v1/admin/import/uploads"),
        new("PUT", "/api/v1/admin/import/uploads"),
        new("DELETE", "/api/v1/admin/import/uploads"),
        new("PATCH", "/api/v1/admin/import/uploads"),
        new("GET", "/api/v1/admin/import/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/jobs/{jobId}"),
        new("PUT", "/api/v1/admin/import/jobs/{jobId}"),
        new("DELETE", "/api/v1/admin/import/jobs/{jobId}"),
        new("PATCH", "/api/v1/admin/import/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("PUT", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("DELETE", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("PATCH", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/jobs"),
        new("POST", "/api/v1/admin/import/jobs"),
        new("PUT", "/api/v1/admin/import/jobs"),
        new("DELETE", "/api/v1/admin/import/jobs"),
        new("PATCH", "/api/v1/admin/import/jobs"),
        new("GET", "/api/v1/admin/import/limits"),
        new("POST", "/api/v1/admin/import/limits"),
        new("PUT", "/api/v1/admin/import/limits"),
        new("DELETE", "/api/v1/admin/import/limits"),
        new("PATCH", "/api/v1/admin/import/limits"),
        new("POST", "/api/v1/admin/import/scan"),

        // v1 admin ArcGIS migration evidence endpoints (#1025 slice 6)
        new("GET", "/api/v1/admin/import/arcgis/migrations"),
        new("POST", "/api/v1/admin/import/arcgis/migrations/{runId}/manifest"),
        new("GET", "/api/v1/admin/import/arcgis/migrations/{runId}/manifest"),
        new("POST", "/api/v1/admin/import/arcgis/migrations/{runId}/parity"),
        new("GET", "/api/v1/admin/import/arcgis/migrations/{runId}/parity"),

        // v1 admin import endpoints (Geoservices)
        new("POST", "/api/v1/admin/import/geoservices/discover"),
        new("POST", "/api/v1/admin/import/geoservices/start"),
        new("GET", "/api/v1/admin/import/geoservices/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/geoservices/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/geoservices/jobs"),

        // v1 admin footprint-driven batch import orchestration (#1253)
        new("POST", "/api/v1/admin/import/migrations"),
        new("GET", "/api/v1/admin/import/migrations/{batchId}"),

        // v1 admin import endpoints (Raster)
        new("POST", "/api/v1/admin/import/raster"),
        new("GET", "/api/v1/admin/import/raster/formats"),
        new("DELETE", "/api/v1/admin/import/raster/{rasterId}"),
        new("PATCH", "/api/v1/admin/import/raster/{rasterId}"),

        // v1 admin migration performance evidence endpoints (#1033 slice 5)
        new("GET", "/api/v1/admin/migration/performance-evidence/latest"),
        new("GET", "/api/v1/admin/migration/performance-evidence/history"),
        new("GET", "/api/v1/admin/migration/performance-evidence/{evidenceId}"),

        // v1 admin import endpoints (GeoServer)
        new("POST", "/api/v1/admin/import/geoserver/discover"),
        new("POST", "/api/v1/admin/import/geoserver/start"),
        new("GET", "/api/v1/admin/import/geoserver/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/geoserver/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/geoserver/jobs"),

        // v1 admin import endpoints (OGC API Features) — #1029 slice 2
        new("POST", "/api/v1/admin/import/ogc-api-features/collection"),
        // v1 admin import endpoints (OGC WFS)
        new("POST", "/api/v1/admin/import/ogc-wfs/start"),
        // v1 admin import endpoints (OGC Coverages - GeoTIFF/COG, issue #1030 slice 2)
        new("POST", "/api/v1/admin/import/ogc/coverages/import"),

        // v1 admin import endpoints (legacy OGC WCS, issue #1030 slice 3)
        new("POST", "/api/v1/admin/import/ogc-wcs/import"),

        // v1 admin GeoServer migration run orchestration endpoints (#1015 slice 5)
        new("GET", "/api/v1/admin/migration/runs"),
        new("POST", "/api/v1/admin/migration/runs"),
        new("GET", "/api/v1/admin/migration/runs/{runId}"),
        new("GET", "/api/v1/admin/migration/runs/{runId}/evidence-pack"),
        new("GET", "/api/v1/admin/migration/runs/{runId}/scorecard"),
        new("POST", "/api/v1/admin/migration/runs/{runId}/complete"),
        new("POST", "/api/v1/admin/migration/runs/{runId}/scorecard"),
        new("POST", "/api/v1/admin/migration/runs/{runId}/cancel"),

        // v1 admin import endpoints (OGC WMTS tile cache export #1016 slice 4)
        new("POST", "/api/v1/admin/import/ogc-tiles/export"),

        // Esri tile/vector-tile cache package import + serving binding (#1269)
        new("POST", "/api/v1/admin/import/tile-package"),
        new("GET", "/tiles/imported/{tileCacheId}"),
        new("GET", "/tiles/imported/{tileCacheId}/{z}/{x}/{y}"),

    ];
}
