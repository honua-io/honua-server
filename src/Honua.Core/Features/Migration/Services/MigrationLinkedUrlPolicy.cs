// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Host and scheme policy for URLs discovered after the operator-submitted service URL
/// has already been validated: OGC API Features next links, redirects, and WMTS tile URLs.
/// A null allowlist does not restrict the host. An empty allowlist rejects every host.
/// HTTPS is required unless <see cref="AllowUnsafeLocalUrls"/> is set.
/// </summary>
internal readonly record struct MigrationLinkedUrlPolicy(
    bool AllowUnsafeLocalUrls,
    IReadOnlyCollection<string>? AllowedHostSuffixes)
{
    private static readonly AsyncLocal<MigrationLinkedUrlPolicy> CurrentSlot = new();

    public static MigrationLinkedUrlPolicy Current
    {
        get => CurrentSlot.Value;
        set => CurrentSlot.Value = value;
    }

    public bool Allows(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (AllowUnsafeLocalUrls)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (AllowedHostSuffixes is null)
        {
            return true;
        }

        return IsHostAllowed(uri.IdnHost, AllowedHostSuffixes);
    }

    public void EnsureAllows(Uri uri)
    {
        if (!Allows(uri))
        {
            throw new HttpRequestException(
                "Migration linked URL is not allowed by the service host policy.");
        }
    }

    private static bool IsHostAllowed(string host, IReadOnlyCollection<string> allowedSuffixes)
    {
        if (string.IsNullOrWhiteSpace(host) || allowedSuffixes.Count == 0)
        {
            return false;
        }

        var normalizedHost = NormalizeHost(host);
        if (normalizedHost.Length == 0)
        {
            return false;
        }

        foreach (var suffix in allowedSuffixes)
        {
            var normalizedSuffix = NormalizeHost(suffix);
            if (normalizedSuffix.Length == 0)
            {
                continue;
            }

            if (normalizedHost == normalizedSuffix ||
                normalizedHost.EndsWith("." + normalizedSuffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeHost(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('.').ToLowerInvariant();
}
