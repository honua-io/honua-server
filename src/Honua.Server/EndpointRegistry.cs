// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

/// <summary>
/// Registry of all public HTTP endpoints exposed by Honua.Server.
/// Keep this list in sync with endpoint mappings to enforce API surface coverage.
/// </summary>
public static class EndpointRegistry
{
    /// <summary>
    /// All endpoints that require integration test coverage.
    /// </summary>
    public static IReadOnlyList<EndpointDefinition> All { get; } =
    [
        new("GET", "/healthz/live"),
        new("GET", "/healthz/ready"),
        new("GET", "/healthz/metrics"),

        new("GET", "/api/v1/admin/config"),
        new("GET", "/api/v1/admin/connections/{id}/tables"),
        new("GET", "/api/v1/admin/connections/{*path}"),
        new("GET", "/api/v1/admin/connections/{id}/layers"),
        new("POST", "/api/v1/admin/connections/{id}/layers"),
        new("PUT", "/api/v1/admin/connections/{id}/layers/{layerId}/enabled"),
        new("GET", "/api/v1/admin/version"),
        new("GET", "/api/v1/admin/capabilities"),
        new("GET", "/api/v1/admin/manifest"),
        new("POST", "/api/v1/admin/manifest/apply"),

        // v1 admin secure connection endpoints
        new("GET", "/api/v1/admin/connections"),
        new("GET", "/api/v1/admin/connections/{id}"),
        new("POST", "/api/v1/admin/connections"),
        new("PUT", "/api/v1/admin/connections/{id}"),
        new("DELETE", "/api/v1/admin/connections/{id}"),
        new("POST", "/api/v1/admin/connections/{id}/test"),
        new("POST", "/api/v1/admin/connections/encryption/validate"),
        new("POST", "/api/v1/admin/connections/encryption/rotate-key"),

        // v1 admin metadata resource endpoints
        new("GET", "/api/v1/admin/metadata/resources"),
        new("GET", "/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}"),
        new("POST", "/api/v1/admin/metadata/resources"),
        new("PUT", "/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}"),
        new("DELETE", "/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}"),

        // v1 admin import endpoints (primary)
        new("GET", "/api/v1/admin/import/formats"),
        new("POST", "/api/v1/admin/import/preview"),
        new("POST", "/api/v1/admin/import/upload"),
        new("GET", "/api/v1/admin/import/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/jobs"),
        new("GET", "/api/v1/admin/import/limits"),

        // v1 admin import endpoints (Esri)
        new("POST", "/api/v1/admin/import/esri/discover"),
        new("POST", "/api/v1/admin/import/esri/start"),
        new("GET", "/api/v1/admin/import/esri/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/esri/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/esri/jobs"),

        // v1 admin operations progress endpoints
        new("GET", "/api/v1/admin/operations/{operationId}"),
        new("POST", "/api/v1/admin/operations/{operationId}/cancel"),
        new("GET", "/api/v1/admin/operations/active"),
        new("GET", "/api/v1/admin/operations/type/{operationType}"),

        new("GET", "/api/v1/admin/performance/database/query-cache/statistics"),

        new("GET", "/api/v1/metrics/health"),
        new("GET", "/api/v1/metrics/performance"),
        new("GET", "/api/v1/metrics/database"),
        new("GET", "/api/v1/metrics/cache"),
        new("GET", "/api/v1/metrics/memory"),

        new("POST", "/csp-violation-report"),

        new("GET", "/rest/services/{serviceId}/FeatureServer"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer"),
        new("GET", "/tiles/{layerId}/{z}/{x}/{y}.mvt"),
        new("GET", "/tiles/{layerId}/tile.json"),
        new("GET", "/api/styles/{layerId}.json"),

        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}"),

        new("GET", "/odata"),
        new("GET", "/odata/$metadata"),
        new("GET", "/odata/Layers"),
        new("GET", "/odata/Layers({layerId})"),
        new("GET", "/odata/Layers/$count"),
        new("GET", "/odata/Layers({layerId})/Features"),
        new("GET", "/odata/Layers({layerId})/Features/$count"),
        new("POST", "/odata/Layers({layerId})/Features"),
        new("GET", "/odata/Layers({layerId})/Features({objectId})"),
        new("PATCH", "/odata/Layers({layerId})/Features({objectId})"),
        new("DELETE", "/odata/Layers({layerId})/Features({objectId})"),
        new("GET", "/odata/Features"),
        new("GET", "/odata/Features/$count"),
        new("POST", "/odata/Features"),
        new("GET", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("PATCH", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("DELETE", "/odata/Features(LayerId={layerId},ObjectId={objectId})"),
        new("GET", "/odata/Features({layerId})"),
        new("GET", "/odata/Features({layerId})/$count"),
        new("GET", "/odata/Features({layerId},{objectId})"),
        new("PATCH", "/odata/Features({layerId},{objectId})"),
        new("DELETE", "/odata/Features({layerId},{objectId})"),
        new("POST", "/odata/$batch"),
        new("GET", "/odata/Features({layerId})/$apply"),
        new("GET", "/odata/Features({layerId})/$search"),

        new("GET", "/ogc/features"),
        new("GET", "/ogc/features/conformance"),
        new("GET", "/openapi.json"),
        new("GET", "/ogc/features/collections"),
        new("GET", "/ogc/features/collections/{collectionId}"),
        new("GET", "/ogc/features/collections/{collectionId}/queryables"),
        new("GET", "/ogc/features/collections/{collectionId}/items"),
        new("GET", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("POST", "/ogc/features/collections/{collectionId}/items"),
        new("POST", "/ogc/features/collections/{collectionId}/items/batch"),
        new("PUT", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("DELETE", "/ogc/features/collections/{collectionId}/items/{featureId}"),

        new("GET", "/ogc/tiles"),
        new("GET", "/ogc/tiles/conformance"),
        new("GET", "/ogc/tiles/openapi.json"),
        new("GET", "/ogc/tiles/collections"),
        new("GET", "/ogc/tiles/collections/{collectionId}"),
        new("GET", "/ogc/tiles/collections/{collectionId}/tiles"),
        new("GET", "/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}"),
        new("GET", "/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}"),
        new("GET", "/ogc/tiles/tiles"),
        new("GET", "/ogc/tiles/tiles/{tileMatrixSetId}"),
        new("GET", "/ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}"),
        new("GET", "/ogc/tiles/tileMatrixSets"),
        new("GET", "/ogc/tiles/tileMatrixSets/{tileMatrixSetId}"),
    ];
}

/// <summary>
/// Describes an HTTP endpoint by method and route pattern.
/// </summary>
/// <param name="Method">HTTP method (GET, POST, etc.).</param>
/// <param name="Path">Route pattern starting with '/'.</param>
public sealed record EndpointDefinition(string Method, string Path);
