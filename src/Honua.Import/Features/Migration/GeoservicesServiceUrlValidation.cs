// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

internal readonly record struct GeoservicesServiceUrlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static GeoservicesServiceUrlValidationResult Success()
        => new(true, null);

    public static GeoservicesServiceUrlValidationResult Failure(string message)
        => new(false, message);
}

internal static class GeoservicesServiceUrlValidation
{
    internal const string InvalidHttpsUrlMessage = "ServiceUrl must be a valid HTTPS URL";
    internal const string EmbeddedCredentialsMessage = "ServiceUrl must not include embedded credentials.";
    internal const string CredentialQueryParameterMessage =
        "ServiceUrl must not include credential query parameters; use the credentials object instead.";
    internal const string InvalidServiceRootMessage =
        "ServiceUrl must target an ArcGIS Service root URL (FeatureServer or MapServer), not a layer/table URL.";
    internal const string DisallowedAddressMessage =
        "ServiceUrl resolves to a private, loopback, or unresolvable network address, which is not allowed.";
    internal const string DisallowedHostMessage =
        "ServiceUrl host is not in the configured Migration:AllowedServiceHostSuffixes allowlist.";

    /// <summary>
    /// Validates the service URL without a host allowlist (any public HTTPS host is permitted).
    /// For deployments that restrict which hosts may be contacted, use the overload that accepts
    /// an <c>allowedHostSuffixes</c> parameter.
    /// </summary>
    public static Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(serviceUrl, allowedHostSuffixes: null, ResolveHostAddressesAsync, cancellationToken);

    /// <summary>
    /// Validates the service URL, optionally enforcing a host-suffix allowlist.
    /// When <paramref name="allowedHostSuffixes"/> is non-null and non-empty, the request host
    /// must exactly match, or be a subdomain of, at least one listed suffix (e.g. ".arcgisonline.com").
    /// An empty allowlist rejects all hosts. A null allowlist allows any public host.
    /// </summary>
    public static Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        IReadOnlyCollection<string>? allowedHostSuffixes,
        CancellationToken cancellationToken = default)
        => ValidateAsync(serviceUrl, allowedHostSuffixes, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
        => await ValidateAsync(serviceUrl, allowedHostSuffixes: null, hostAddressResolver, cancellationToken).ConfigureAwait(false);

    internal static async Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        IReadOnlyCollection<string>? allowedHostSuffixes,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return GeoservicesServiceUrlValidationResult.Failure(InvalidHttpsUrlMessage);
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return GeoservicesServiceUrlValidationResult.Failure(EmbeddedCredentialsMessage);
        }

        if (HasCredentialQueryParameter(uri))
        {
            return GeoservicesServiceUrlValidationResult.Failure(CredentialQueryParameterMessage);
        }

        if (!IsServiceRootUrl(uri))
        {
            return GeoservicesServiceUrlValidationResult.Failure(InvalidServiceRootMessage);
        }

        if (await NetworkAddressValidator.IsDisallowedAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return GeoservicesServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        // PA-153: optional per-deployment host allowlist. When configured, only URLs whose host
        // exactly matches a listed suffix, or is below it on a DNS label boundary, are accepted.
        // This prevents credentialed SSRF to arbitrary public internet hosts if admin credentials are compromised.
        if (allowedHostSuffixes is not null && !IsHostAllowed(uri.Host, allowedHostSuffixes))
        {
            return GeoservicesServiceUrlValidationResult.Failure(DisallowedHostMessage);
        }

        return GeoservicesServiceUrlValidationResult.Success();
    }

    internal static bool IsHostAllowed(string host, IReadOnlyCollection<string> allowedSuffixes)
    {
        if (string.IsNullOrWhiteSpace(host) || allowedSuffixes.Count == 0)
        {
            return false;
        }

        var normalizedHost = NormalizeHostAllowlistToken(host);
        if (normalizedHost.Length == 0)
        {
            return false;
        }

        foreach (var suffix in allowedSuffixes)
        {
            var normalizedSuffix = NormalizeHostAllowlistToken(suffix);
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

    private static string NormalizeHostAllowlistToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Trim('.').ToLowerInvariant();

    private static bool IsServiceRootUrl(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        var lastSegment = segments[^1];
        return lastSegment.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
               || lastSegment.Equals("MapServer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCredentialQueryParameter(Uri uri)
    {
        var query = uri.Query;
        if (query.Length <= 1)
        {
            return false;
        }

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
            var decodedName = DecodeQueryName(name);
            if (IsCredentialQueryName(decodedName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCredentialQueryName(string name)
        => name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("password", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("pass", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("pwd", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("apiKey", StringComparison.OrdinalIgnoreCase);

    private static string DecodeQueryName(string name)
    {
        try
        {
            return Uri.UnescapeDataString(name.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return name;
        }
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

}
