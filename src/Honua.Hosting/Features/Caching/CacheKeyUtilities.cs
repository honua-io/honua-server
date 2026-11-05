// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal static class CacheKeyUtilities
{
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
