// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Caching;

/// <summary>
/// Input used to build a stable metadata cache key.
/// </summary>
public sealed record MetadataCacheKeyRequest
{
    /// <summary>
    /// Redis key prefix for metadata cache entries.
    /// </summary>
    public string KeyPrefix { get; init; } = MetadataCacheKeyBuilder.DefaultKeyPrefix;

    /// <summary>
    /// Tenant identifier that owns the metadata entry.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Project identifier that owns the metadata entry.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Authorization scope, role set, or permission fingerprint input for the caller.
    /// </summary>
    public string? AuthScope { get; init; }

    /// <summary>
    /// Source endpoint address. The raw value is fingerprinted and never emitted in the key.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Adapter implementation name, such as <c>geoservices</c>, <c>ogc-api</c>, or <c>stac</c>.
    /// </summary>
    public string? Adapter { get; init; }

    /// <summary>
    /// Protocol surface used by the adapter, such as <c>featureserver</c>, <c>wfs</c>, or <c>stac</c>.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Logical metadata resource kind, such as <c>service</c>, <c>layer</c>, or <c>collection</c>.
    /// </summary>
    public string? ResourceKind { get; init; }

    /// <summary>
    /// Resource identifier within the source system.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Coordinate reference system that changes the metadata response.
    /// </summary>
    public string? Crs { get; init; }

    /// <summary>
    /// Representation format or negotiated media type.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Adapter contract or implementation version, when it changes serialized metadata.
    /// </summary>
    public string? AdapterVersion { get; init; }

    /// <summary>
    /// Projection database or CRS transformation version, when it changes serialized metadata.
    /// </summary>
    public string? ProjectionVersion { get; init; }
}

/// <summary>
/// Built metadata cache key and safe fingerprints for observability and invalidation.
/// </summary>
public sealed record MetadataCacheKey(
    string Value,
    string KeyFingerprint,
    string SourceFingerprint,
    string AuthScopeFingerprint)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Builds deterministic Redis-safe metadata cache keys without exposing source addresses or credentials.
/// </summary>
public static class MetadataCacheKeyBuilder
{
    /// <summary>
    /// Default namespace for metadata cache entries.
    /// </summary>
    public const string DefaultKeyPrefix = "honua:metadata";

    private const string KeyVersion = "v1";
    private const int FingerprintLength = 32;
    private const int MaxComponentLength = 96;

    private static readonly string[] CredentialQueryNames =
    [
        "access_token",
        "access-token",
        "api_key",
        "api-key",
        "apikey",
        "auth",
        "authorization",
        "client_secret",
        "client-secret",
        "code",
        "key",
        "sas",
        "password",
        "passwd",
        "pwd",
        "secret",
        "session",
        "sessionid",
        "sig",
        "signature",
        "subscription-key",
        "token",
        "x-api-key"
    ];

    /// <summary>
    /// Builds the full key and safe fingerprints for the supplied metadata cache request.
    /// </summary>
    public static MetadataCacheKey Build(MetadataCacheKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceFingerprint = FingerprintSource(request.SourceUrl);
        var authScopeFingerprint = FingerprintAuthScope(request.AuthScope);

        var parts = new[]
        {
            NormalizePrefix(request.KeyPrefix),
            KeyVersion,
            "tenant",
            NormalizeKeyComponent(request.TenantId, "global"),
            "project",
            NormalizeKeyComponent(request.ProjectId, "global"),
            "auth",
            authScopeFingerprint,
            "source",
            sourceFingerprint,
            "adapter",
            NormalizeKeyComponent(request.Adapter, "unknown"),
            "protocol",
            NormalizeKeyComponent(request.Protocol, "unknown"),
            "kind",
            NormalizeKeyComponent(request.ResourceKind, "metadata"),
            "resource",
            NormalizeKeyComponent(request.ResourceId, "root"),
            "crs",
            NormalizeKeyComponent(request.Crs, "default"),
            "format",
            NormalizeKeyComponent(request.Format, "default"),
            "adapter-version",
            NormalizeKeyComponent(request.AdapterVersion, "default"),
            "projection-version",
            NormalizeKeyComponent(request.ProjectionVersion, "default")
        };

        var key = string.Join(':', parts);
        return new MetadataCacheKey(
            key,
            Fingerprint(key),
            sourceFingerprint,
            authScopeFingerprint);
    }

    /// <summary>
    /// Builds the full Redis-safe metadata cache key value.
    /// </summary>
    public static string BuildKey(MetadataCacheKeyRequest request) => Build(request).Value;

    /// <summary>
    /// Builds the source endpoint fingerprint used by keys and invalidation events.
    /// </summary>
    public static string FingerprintSource(string? sourceUrl)
    {
        var canonicalSource = NormalizeSourceForFingerprint(sourceUrl);
        return Fingerprint(canonicalSource);
    }

    /// <summary>
    /// Builds the authorization-scope fingerprint used by metadata cache keys.
    /// </summary>
    public static string FingerprintAuthScope(string? authScope)
    {
        var canonicalScope = NormalizeText(authScope, "public");
        return Fingerprint(canonicalScope);
    }

    internal static string NormalizeForMatch(string? value, string fallback) => NormalizeKeyComponent(value, fallback);

    private static string NormalizePrefix(string? prefix)
    {
        var normalized = string.IsNullOrWhiteSpace(prefix)
            ? DefaultKeyPrefix
            : prefix.Trim().ToLowerInvariant();

        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (IsSafeKeyCharacter(c) || c == ':' || c == '/')
            {
                builder.Append(c == '/' ? ':' : c);
            }
            else if (char.IsWhiteSpace(c))
            {
                builder.Append(':');
            }
        }

        var result = CollapseRepeatedSeparators(builder.ToString(), ':').Trim(':');
        return string.IsNullOrEmpty(result) ? DefaultKeyPrefix : result;
    }

    private static string NormalizeKeyComponent(string? value, string fallback)
    {
        var normalized = NormalizeText(value, fallback);
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

        return string.Concat(component.AsSpan(0, 63), "-", Fingerprint(component).AsSpan(0, 16));
    }

    private static string NormalizeText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;

        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(c);
                previousWasWhitespace = false;
            }
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    private static string NormalizeSourceForFingerprint(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return "none";
        }

        var trimmed = NormalizeText(sourceUrl, "none");
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var path = NormalizeSourcePath(uri.AbsolutePath);
        var query = NormalizeSourceQuery(uri.Query);
        var port = uri.IsDefaultPort ? string.Empty : FormattableString.Invariant($":{uri.Port}");
        var canonical = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}";

        return string.IsNullOrEmpty(query) ? canonical : $"{canonical}?{query}";
    }

    private static string NormalizeSourcePath(string path)
    {
        var decodedPath = Uri.UnescapeDataString(path);
        var normalizedPath = NormalizeText(decodedPath, "/").Replace(' ', '-').TrimEnd('/');
        return string.IsNullOrEmpty(normalizedPath) ? "/" : normalizedPath;
    }

    private static string NormalizeSourceQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var pairs = new List<string>();
        var trimmedQuery = query.TrimStart('?');
        foreach (var segment in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = segment.IndexOf('=', StringComparison.Ordinal);
            var rawName = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
            var rawValue = equalsIndex >= 0 ? segment[(equalsIndex + 1)..] : string.Empty;
            var name = NormalizeText(Uri.UnescapeDataString(rawName), string.Empty);

            if (string.IsNullOrEmpty(name) || IsCredentialQueryName(name))
            {
                continue;
            }

            var value = NormalizeText(Uri.UnescapeDataString(rawValue), string.Empty);
            pairs.Add($"{name}={value}");
        }

        pairs.Sort(StringComparer.Ordinal);
        return string.Join('&', pairs);
    }

    private static bool IsCredentialQueryName(string name)
    {
        return Array.Exists(CredentialQueryNames, candidate => string.Equals(candidate, name, StringComparison.Ordinal));
    }

    private static string Fingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..FingerprintLength];
    }

    private static bool IsSafeKeyCharacter(char c)
    {
        return c is >= 'a' and <= 'z'
            || c is >= '0' and <= '9'
            || c is '-'
            || c is '_'
            || c is '.';
    }

    private static string CollapseRepeatedSeparators(string value, char separator)
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
                    previousWasSeparator = true;
                }
            }
            else
            {
                builder.Append(c);
                previousWasSeparator = false;
            }
        }

        return builder.ToString();
    }
}
