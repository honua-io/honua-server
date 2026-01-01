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

        new("GET", "/api/admin/connections/{id}/tables"),
        new("GET", "/api/admin/connections/{*path}"),

        // v1 admin metadata endpoints
        new("GET", "/api/v1/admin/metadata/services"),
        new("GET", "/api/v1/admin/metadata/services/{name}"),
        new("POST", "/api/v1/admin/metadata/services"),
        new("PUT", "/api/v1/admin/metadata/services/{name}"),
        new("DELETE", "/api/v1/admin/metadata/services/{name}"),
        new("POST", "/api/v1/admin/metadata/services/{name}/layers"),
        new("DELETE", "/api/v1/admin/metadata/services/{name}/layers/{layerId}"),
        new("GET", "/api/v1/admin/metadata/layers"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}"),
        new("POST", "/api/v1/admin/metadata/layers"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}"),
        new("DELETE", "/api/v1/admin/metadata/layers/{layerId}"),
        new("POST", "/api/v1/admin/metadata/layers/{layerId}/refresh"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/relationships"),
        new("POST", "/api/v1/admin/metadata/layers/{layerId}/relationships"),
        new("DELETE", "/api/v1/admin/metadata/layers/{layerId}/relationships/{relationshipId}"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/style"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/style"),

        // v1 admin import endpoints (primary)
        new("GET", "/api/v1/admin/import/formats"),
        new("POST", "/api/v1/admin/import/preview"),
        new("POST", "/api/v1/admin/import/upload"),
        new("GET", "/api/v1/admin/import/jobs/{jobId}"),
        new("POST", "/api/v1/admin/import/jobs/{jobId}/cancel"),
        new("GET", "/api/v1/admin/import/jobs"),
        new("GET", "/api/v1/admin/import/limits"),

        // Legacy import endpoints (backward compatibility aliases)
        new("GET", "/api/import/formats"),
        new("POST", "/api/import/preview"),
        new("POST", "/api/import/upload"),
        new("GET", "/api/import/jobs/{jobId}"),
        new("POST", "/api/import/jobs/{jobId}/cancel"),
        new("GET", "/api/import/jobs"),
        new("GET", "/api/import/limits"),

        new("GET", "/rest/services/{serviceId}/FeatureServer"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/query"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords"),
        new("GET", "/tiles/{layerId}/{z}/{x}/{y}.mvt"),

        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment"),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments"),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}"),

        new("GET", "/odata"),
        new("GET", "/odata/$metadata"),
        new("GET", "/odata/Layers"),
        new("GET", "/odata/Features({layerId})"),
        new("POST", "/odata/Features({layerId})"),
        new("GET", "/odata/Features({layerId},{objectId})"),
        new("PATCH", "/odata/Features({layerId},{objectId})"),
        new("DELETE", "/odata/Features({layerId},{objectId})"),

        new("GET", "/ogc/features"),
        new("GET", "/ogc/features/conformance"),
        new("GET", "/openapi.json"),
        new("GET", "/ogc/features/collections"),
        new("GET", "/ogc/features/collections/{collectionId}"),
        new("GET", "/ogc/features/collections/{collectionId}/items"),
        new("GET", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("POST", "/ogc/features/collections/{collectionId}/items"),
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
