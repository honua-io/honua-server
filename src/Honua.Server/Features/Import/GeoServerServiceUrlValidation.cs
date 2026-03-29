// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.Server.Features.Import;

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

    public static Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(geoServerRestUrl, allowUnsafeLocalUrls: false, ResolveHostAddressesAsync, cancellationToken);

    public static Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken = default)
        => ValidateAsync(geoServerRestUrl, allowUnsafeLocalUrls, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<GeoServerServiceUrlValidationResult> ValidateAsync(
        string geoServerRestUrl,
        bool allowUnsafeLocalUrls,
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
