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

        new("GET", "/api/import/formats"),
        new("POST", "/api/import/preview"),
        new("POST", "/api/import/upload"),

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
    ];
}

/// <summary>
/// Describes an HTTP endpoint by method and route pattern.
/// </summary>
/// <param name="Method">HTTP method (GET, POST, etc.).</param>
/// <param name="Path">Route pattern starting with '/'.</param>
public sealed record EndpointDefinition(string Method, string Path);
