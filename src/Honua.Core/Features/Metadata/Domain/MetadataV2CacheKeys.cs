// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Metadata v2 cache entry family.
/// </summary>
public enum MetadataV2CacheEntryKind
{
    /// <summary>
    /// Versioned metadata snapshot.
    /// </summary>
    Snapshot,

    /// <summary>
    /// Derived metadata projection.
    /// </summary>
    Projection
}

/// <summary>
/// Request used to build metadata v2 snapshot and projection cache keys.
/// </summary>
public sealed record MetadataV2CacheKeyRequest
{
    /// <summary>
    /// Cache key namespace prefix.
    /// </summary>
    public string KeyPrefix { get; init; } = MetadataV2CacheKeyBuilder.DefaultKeyPrefix;

    /// <summary>
    /// Tenant dimension.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Environment dimension, for example <c>dev</c>, <c>staging</c>, or <c>prod</c>.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Catalog dimension.
    /// </summary>
    public string? CatalogId { get; init; }

    /// <summary>
    /// Metadata schema version dimension.
    /// </summary>
    public string? SchemaVersion { get; init; }

    /// <summary>
    /// Metadata revision dimension.
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>
    /// Projection target dimension.
    /// </summary>
    public string? ProjectionTarget { get; init; }

    /// <summary>
    /// Projection profile version dimension.
    /// </summary>
    public string? ProjectionProfileVersion { get; init; }
}

/// <summary>
/// Built metadata v2 cache key.
/// </summary>
public sealed record MetadataV2CacheKey(MetadataV2CacheEntryKind Kind, string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Builds deterministic metadata v2 cache keys for snapshots and projections.
/// </summary>
public static class MetadataV2CacheKeyBuilder
{
    /// <summary>
    /// Default namespace for metadata v2 cache entries.
    /// </summary>
    public const string DefaultKeyPrefix = "honua:metadata:v2";

    private const int MaxComponentLength = 96;
    private const int ComponentFingerprintLength = 16;

    /// <summary>
    /// Builds a metadata v2 snapshot cache key.
    /// </summary>
    public static MetadataV2CacheKey BuildSnapshot(MetadataV2CacheKeyRequest request)
        => Build(MetadataV2CacheEntryKind.Snapshot, request);

    /// <summary>
    /// Builds a metadata v2 projection cache key.
    /// </summary>
    public static MetadataV2CacheKey BuildProjection(MetadataV2CacheKeyRequest request)
        => Build(MetadataV2CacheEntryKind.Projection, request);

    private static MetadataV2CacheKey Build(MetadataV2CacheEntryKind kind, MetadataV2CacheKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parts = new List<string>
        {
            NormalizePrefix(request.KeyPrefix),
            NormalizeComponent(kind.ToString(), "unknown"),
            "tenant",
            NormalizeScopedComponent(request.TenantId),
            "environment",
            NormalizeScopedComponent(request.Environment),
            "catalog",
            NormalizeScopedComponent(request.CatalogId),
            "schema",
            NormalizeScopedComponent(request.SchemaVersion),
            "revision",
            NormalizeScopedComponent(request.Revision)
        };

        if (kind == MetadataV2CacheEntryKind.Projection)
        {
            parts.Add("target");
            parts.Add(NormalizeScopedComponent(request.ProjectionTarget));
            parts.Add("profile");
            parts.Add(NormalizeScopedComponent(request.ProjectionProfileVersion));
        }

        return new MetadataV2CacheKey(kind, string.Join(':', parts));
    }

    private static string NormalizePrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultKeyPrefix;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (IsSafeKeyCharacter(c) || c == ':')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsWhiteSpace(c) || c == '/')
            {
                builder.Append(':');
            }
            else
            {
                builder.Append('-');
            }
        }

        var normalized = CollapseSeparators(builder.ToString(), ':').Trim(':');
        return string.IsNullOrEmpty(normalized) ? DefaultKeyPrefix : normalized;
    }

    private static string NormalizeScopedComponent(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : $"id-{NormalizeComponent(value, "none")}";

    private static string NormalizeComponent(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousWasDash = false;
        foreach (var c in normalized)
        {
            if (IsSafeKeyCharacter(c))
            {
                builder.Append(c);
                previousWasDash = false;
            }
            else if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        var component = builder.ToString().Trim('-');
        if (string.IsNullOrEmpty(component))
        {
            return fallback;
        }

        if (component.Length <= MaxComponentLength)
        {
            return component;
        }

        var prefixLength = MaxComponentLength - ComponentFingerprintLength - 1;
        return string.Concat(
            component.AsSpan(0, prefixLength).TrimEnd('-'),
            "-",
            Fingerprint(component).AsSpan(0, ComponentFingerprintLength));
    }

    private static string Fingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CollapseSeparators(string value, char separator)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var c in value)
        {
            if (c == separator)
            {
                if (!previousWasSeparator)
                {
                    builder.Append(c);
                }

                previousWasSeparator = true;
                continue;
            }

            builder.Append(c);
            previousWasSeparator = false;
        }

        return builder.ToString();
    }

    private static bool IsSafeKeyCharacter(char c)
        => (c >= 'a' && c <= 'z') ||
            (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') ||
            c == '.' ||
            c == '_' ||
            c == '-';
}
