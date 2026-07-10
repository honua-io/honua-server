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

internal readonly record struct GeoServerServiceUrlValidationResult(bool IsValid, string? ErrorMessage)
{
    public static GeoServerServiceUrlValidationResult Success()
        => new(true, null);

    public static GeoServerServiceUrlValidationResult Failure(string message)
        => new(false, message);
}

internal static class GeoServerServiceUrlValidation
{
    internal const string InvalidHttpsUrlMessage = "GeoServerRestUrl must be a valid HTTPS URL";
    internal const string InvalidHttpOrHttpsUrlMessage =
        "GeoServerRestUrl must be a valid HTTP or HTTPS URL when unsafe test GeoServer URLs are enabled.";
    internal const string EmbeddedCredentialsMessage = "GeoServerRestUrl must not include embedded credentials.";
    internal const string DisallowedAddressMessage =
        "GeoServerRestUrl resolves to a private, loopback, or unresolvable network address, which is not allowed.";
    internal const string DisallowedHostMessage =
        "GeoServerRestUrl host is not in the configured Migration:AllowedServiceHostSuffixes allowlist.";

    /// <inheritdoc cref="GeoservicesServiceUrlValidation.ValidateAsync(string,IReadOnlyCollection{string}?,CancellationToken)"/>
    public static Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(geoServerRestUrl, allowUnsafeLocalUrls: false, allowedHostSuffixes: null, ResolveHostAddressesAsync, cancellationToken);

    /// <inheritdoc cref="GeoservicesServiceUrlValidation.ValidateAsync(string,IReadOnlyCollection{string}?,CancellationToken)"/>
    public static Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken = default)
        => ValidateAsync(geoServerRestUrl, allowUnsafeLocalUrls, allowedHostSuffixes: null, ResolveHostAddressesAsync, cancellationToken);

    /// <inheritdoc cref="GeoservicesServiceUrlValidation.ValidateAsync(string,IReadOnlyCollection{string}?,CancellationToken)"/>
    public static Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        bool allowUnsafeLocalUrls,
        IReadOnlyCollection<string>? allowedHostSuffixes,
        CancellationToken cancellationToken = default)
        => ValidateAsync(geoServerRestUrl, allowUnsafeLocalUrls, allowedHostSuffixes, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        bool allowUnsafeLocalUrls,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
        => await ValidateAsync(geoServerRestUrl, allowUnsafeLocalUrls, allowedHostSuffixes: null, hostAddressResolver, cancellationToken).ConfigureAwait(false);

    internal static async Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        bool allowUnsafeLocalUrls,
        IReadOnlyCollection<string>? allowedHostSuffixes,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(geoServerRestUrl, UriKind.Absolute, out var uri))
        {
            return GeoServerServiceUrlValidationResult.Failure(
                allowUnsafeLocalUrls ? InvalidHttpOrHttpsUrlMessage : InvalidHttpsUrlMessage);
        }

        if (allowUnsafeLocalUrls)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return GeoServerServiceUrlValidationResult.Failure(InvalidHttpOrHttpsUrlMessage);
            }
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return GeoServerServiceUrlValidationResult.Failure(InvalidHttpsUrlMessage);
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return GeoServerServiceUrlValidationResult.Failure(EmbeddedCredentialsMessage);
        }

        // An explicitly configured allowlist remains authoritative even when the
        // test-only unsafe-local switch relaxes scheme and address validation.
        if (allowedHostSuffixes is not null &&
            !GeoservicesServiceUrlValidation.IsHostAllowed(uri.Host, allowedHostSuffixes))
        {
            return GeoServerServiceUrlValidationResult.Failure(DisallowedHostMessage);
        }

        if (allowUnsafeLocalUrls)
        {
            return GeoServerServiceUrlValidationResult.Success();
        }

        if (await NetworkAddressValidator.IsDisallowedAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return GeoServerServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return GeoServerServiceUrlValidationResult.Success();
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}
