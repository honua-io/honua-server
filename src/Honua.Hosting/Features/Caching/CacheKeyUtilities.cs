// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Shared cache-key classification utilities used by both the cache service
/// and the background refresh coordinator for metrics tagging.
/// </summary>
internal static class CacheKeyUtilities
{
    /// <summary>
    /// Classifies a cache key into a family name for performance-metric tagging.
    /// </summary>
    internal static string GetCacheKeyFamily(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "empty";
        }

        return key switch
        {
            _ when key.Contains("layer", StringComparison.OrdinalIgnoreCase) => "layer",
            _ when key.Contains("service", StringComparison.OrdinalIgnoreCase) => "service",
            _ when key.Contains("query", StringComparison.OrdinalIgnoreCase) => "query",
            _ when key.Contains("tile", StringComparison.OrdinalIgnoreCase) || key.Contains("mvt", StringComparison.OrdinalIgnoreCase) => "tile",
            _ when key.Contains("catalog", StringComparison.OrdinalIgnoreCase) => "catalog",
            _ when key.Contains("replica", StringComparison.OrdinalIgnoreCase) => "replica",
            _ when key.Contains("schema", StringComparison.OrdinalIgnoreCase) => "schema",
            _ when key.Contains("auth", StringComparison.OrdinalIgnoreCase) => "auth",
            _ => "general"
        };
    }
}
