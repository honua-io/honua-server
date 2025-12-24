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

        new("GET", "/odata"),
        new("GET", "/odata/$metadata"),
        new("GET", "/odata/Layers"),
        new("GET", "/odata/Features({layerId})"),
    ];
}

/// <summary>
/// Describes an HTTP endpoint by method and route pattern.
/// </summary>
/// <param name="Method">HTTP method (GET, POST, etc.).</param>
/// <param name="Path">Route pattern starting with '/'.</param>
public sealed record EndpointDefinition(string Method, string Path);
