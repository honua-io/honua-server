// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Helpers for applying schema-aware cache key scoping consistently.
/// </summary>
internal static class CacheScopeKeys
{
    private const string DefaultScopePrefix = "scope:default:";
    private const string SchemaScopePrefix = "scope:schema:";

    internal static string GetScopePrefix(string? schema)
    {
        return string.IsNullOrWhiteSpace(schema)
            ? DefaultScopePrefix
            : $"{SchemaScopePrefix}{schema.Trim().ToLowerInvariant()}:";
    }

    internal static string EnsureScoped(string key, string? schema)
    {
        return IsScoped(key)
            ? key
            : $"{GetScopePrefix(schema)}{key}";
    }

    internal static bool IsScoped(string key)
    {
        return key.StartsWith(DefaultScopePrefix, StringComparison.Ordinal)
            || key.StartsWith(SchemaScopePrefix, StringComparison.Ordinal);
    }
}
