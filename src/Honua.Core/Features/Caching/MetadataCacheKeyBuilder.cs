// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Caching;

public sealed record MetadataCacheKeyRequest
{
    public string KeyPrefix { get; init; } = MetadataCacheKeyBuilder.DefaultKeyPrefix;

    public string? TenantId { get; init; }

    public string? ProjectId { get; init; }

    public string? AuthScope { get; init; }

    // The source endpoint is fingerprinted into the cache key and the raw value is never emitted.
    public string? SourceUrl { get; init; }

    public string? Adapter { get; init; }

    public string? Protocol { get; init; }

    public string? ResourceKind { get; init; }

    public string? ResourceId { get; init; }

    public string? Crs { get; init; }

    public string? Format { get; init; }

    public string? AdapterVersion { get; init; }

    public string? ProjectionVersion { get; init; }
}

public sealed record MetadataCacheKey(
    string Value,
    string KeyFingerprint,
    string SourceFingerprint,
    string AuthScopeFingerprint)
{
    public override string ToString() => Value;
}

public static class MetadataCacheKeyBuilder
{
    public const string DefaultKeyPrefix = "honua:metadata";

    private const string KeyVersion = "v1";
    private const int FingerprintLength = 32;
    private const int MaxComponentLength = 96;
    private const string MissingScopedComponent = "none";
    private const string PresentScopedComponentPrefix = "id-";

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
            NormalizeScopedKeyComponent(request.TenantId),
            "project",
            NormalizeScopedKeyComponent(request.ProjectId),
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
            NormalizeScopedKeyComponent(request.ResourceId),
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

    public static string BuildKey(MetadataCacheKeyRequest request) => Build(request).Value;

    public static string FingerprintSource(string? sourceUrl)
    {
        var canonicalSource = NormalizeSourceForFingerprint(sourceUrl);
        return Fingerprint(canonicalSource);
    }

    public static string FingerprintAuthScope(string? authScope)
    {
        var canonicalScope = NormalizeText(authScope, "public");
        return Fingerprint(canonicalScope);
    }

    internal static string NormalizeForMatch(string? value, string fallback) => NormalizeKeyComponent(value, fallback);

    internal static string NormalizeScopedForMatch(string? value) => NormalizeScopedKeyComponent(value);

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

    private static string NormalizeScopedKeyComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MissingScopedComponent;
        }

        return PresentScopedComponentPrefix + NormalizeKeyComponent(value, "unnamed");
    }

    private static string NormalizeText(string? value, string fallback)
        => NormalizeTextCore(value, fallback, preserveCase: false);

    private static string NormalizeSourceText(string? value, string fallback)
        => NormalizeTextCore(value, fallback, preserveCase: true);

    private static string NormalizeTextCore(string? value, string fallback, bool preserveCase)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (!preserveCase)
        {
            normalized = normalized.ToLowerInvariant();
        }

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

        var trimmed = NormalizeSourceText(sourceUrl, "none");
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
        var normalizedPath = NormalizeSourceText(decodedPath, "/").TrimEnd('/');
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
            var name = NormalizeSourceText(Uri.UnescapeDataString(rawName), string.Empty);
            var credentialName = NormalizeText(Uri.UnescapeDataString(rawName), string.Empty);

            if (string.IsNullOrEmpty(name) || IsCredentialQueryName(credentialName))
            {
                continue;
            }

            var value = NormalizeSourceText(Uri.UnescapeDataString(rawValue), string.Empty);
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
