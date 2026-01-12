// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Utility for classifying HTTP requests by protocol and operation type for performance monitoring.
/// Enables protocol-specific performance tracking and analysis.
/// </summary>
public static class ProtocolClassifier
{
    /// <summary>
    /// Classifies an HTTP request path into protocol and operation categories.
    /// </summary>
    /// <param name="path">The HTTP request path</param>
    /// <returns>A tuple containing the protocol and operation classification</returns>
    /// <remarks>
    /// This classification enables granular performance monitoring across different
    /// geospatial protocols supported by Honua Server.
    /// </remarks>
    public static (string Protocol, string Operation) ClassifyRequest(string path)
    {
        if (string.IsNullOrEmpty(path))
            return ("unknown", "unknown");

        var normalizedPath = path.ToLowerInvariant();

        // GeoServices REST API (Esri/ArcGIS compatibility)
        if (normalizedPath.Contains("/rest/services/"))
        {
            var operation = ClassifyGeoServicesOperation(normalizedPath);
            return ("feature-server", operation);
        }

        // OGC API Features
        if (normalizedPath.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) || normalizedPath.Contains("/collections/"))
        {
            var operation = ClassifyOgcFeaturesOperation(normalizedPath);
            return ("ogc-features", operation);
        }

        // OGC API Tiles / MVT
        if (normalizedPath.Contains("/tiles/") || normalizedPath.Contains(".mvt"))
        {
            var operation = ClassifyOgcTilesOperation(normalizedPath);
            return ("ogc-tiles", operation);
        }

        // OData v4
        if (normalizedPath.StartsWith("/odata/", StringComparison.OrdinalIgnoreCase))
        {
            var operation = ClassifyODataOperation(normalizedPath);
            return ("odata", operation);
        }

        // Admin API
        if (normalizedPath.StartsWith("/api/v", StringComparison.OrdinalIgnoreCase))
        {
            var operation = ClassifyAdminOperation(normalizedPath);
            return ("admin-api", operation);
        }

        // Health checks
        if (normalizedPath.Contains("/health"))
        {
            return ("health", "check");
        }

        return ("unknown", "unknown");
    }

    /// <summary>
    /// Classifies GeoServices REST operations by analyzing the request path.
    /// </summary>
    /// <param name="path">Normalized request path</param>
    /// <returns>Operation type classification</returns>
    private static string ClassifyGeoServicesOperation(string path)
    {
        return path switch
        {
            _ when path.Contains("/query") => "query",
            _ when path.Contains("/applyedits") => "edit",
            _ when path.Contains("/addattachment") => "attachment-add",
            _ when path.Contains("/deleteattachments") => "attachment-delete",
            _ when path.Contains("/updateattachment") => "attachment-update",
            _ when path.Contains("/attachments") => "attachment-query",
            _ when path.Contains("/queryrelated") => "related-query",
            _ when path.EndsWith("/featureserver", StringComparison.OrdinalIgnoreCase) => "metadata",
            _ when path.Contains("/layers") => "metadata",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Classifies OGC API Features operations by analyzing the request path.
    /// </summary>
    /// <param name="path">Normalized request path</param>
    /// <returns>Operation type classification</returns>
    private static string ClassifyOgcFeaturesOperation(string path)
    {
        return path switch
        {
            _ when path.EndsWith("/collections", StringComparison.OrdinalIgnoreCase) => "collections",
            _ when path.Contains("/collections/") && path.Contains("/items/") => "item-get",
            _ when path.Contains("/collections/") && path.EndsWith("/items", StringComparison.OrdinalIgnoreCase) => "items-query",
            _ when path.Contains("/conformance") => "conformance",
            _ when path.Contains("/api") => "openapi",
            _ when path.EndsWith("/ogc/", StringComparison.OrdinalIgnoreCase) => "landing-page",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Classifies OGC API Tiles operations by analyzing the request path.
    /// </summary>
    /// <param name="path">Normalized request path</param>
    /// <returns>Operation type classification</returns>
    private static string ClassifyOgcTilesOperation(string path)
    {
        return path switch
        {
            _ when path.Contains(".mvt") => "tile-mvt",
            _ when path.Contains("/tiles/") && path.Contains("/tilesets") => "tileset-metadata",
            _ when path.Contains("/tiles/") => "tile-metadata",
            _ when path.Contains("/tilematrixsets") => "matrix-sets",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Classifies OData operations by analyzing the request path and HTTP method.
    /// </summary>
    /// <param name="path">Normalized request path</param>
    /// <returns>Operation type classification</returns>
    private static string ClassifyODataOperation(string path)
    {
        return path switch
        {
            _ when path.Contains("$metadata") => "metadata",
            _ when path.Contains("$batch") => "batch",
            _ when path.Contains("$count") => "count",
            _ when path.Contains("$filter") => "filter-query",
            _ when path.Contains("$orderby") => "ordered-query",
            _ when path.Contains("$top") || path.Contains("$skip") => "paged-query",
            _ when path.Contains("$select") => "projection-query",
            _ => "entity-query"
        };
    }

    /// <summary>
    /// Classifies Admin API operations by analyzing the request path.
    /// </summary>
    /// <param name="path">Normalized request path</param>
    /// <returns>Operation type classification</returns>
    private static string ClassifyAdminOperation(string path)
    {
        return path switch
        {
            _ when path.Contains("/import") => "import",
            _ when path.Contains("/layers") => "layer-management",
            _ when path.Contains("/connections") => "connection-management",
            _ when path.Contains("/config") => "configuration",
            _ when path.Contains("/metrics") => "monitoring",
            _ when path.Contains("/security") => "security-management",
            _ => "admin-general"
        };
    }

    /// <summary>
    /// Gets all supported protocol types.
    /// </summary>
    /// <returns>Array of supported protocol identifiers</returns>
    public static string[] GetSupportedProtocols()
    {
        return new[]
        {
            "feature-server",
            "ogc-features",
            "ogc-tiles",
            "odata",
            "admin-api",
            "health"
        };
    }

    /// <summary>
    /// Gets all operation types for a specific protocol.
    /// </summary>
    /// <param name="protocol">Protocol identifier</param>
    /// <returns>Array of operation types for the protocol</returns>
    public static string[] GetOperationsForProtocol(string protocol)
    {
        return protocol.ToLowerInvariant() switch
        {
            "feature-server" => new[] { "query", "edit", "attachment-add", "attachment-delete", "attachment-update", "attachment-query", "related-query", "metadata" },
            "ogc-features" => new[] { "collections", "item-get", "items-query", "conformance", "openapi", "landing-page" },
            "ogc-tiles" => new[] { "tile-mvt", "tileset-metadata", "tile-metadata", "matrix-sets" },
            "odata" => new[] { "metadata", "batch", "count", "filter-query", "ordered-query", "paged-query", "projection-query", "entity-query" },
            "admin-api" => new[] { "import", "layer-management", "connection-management", "configuration", "monitoring", "security-management", "admin-general" },
            "health" => new[] { "check" },
            _ => Array.Empty<string>()
        };
    }
}
