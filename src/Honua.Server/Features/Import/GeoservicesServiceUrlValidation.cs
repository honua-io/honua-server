// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.Server.Features.Import;

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
    internal const string DisallowedAddressMessage =
        "ServiceUrl resolves to a private, loopback, or unresolvable network address, which is not allowed.";

    public static Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
        CancellationToken cancellationToken = default)
        => ValidateAsync(serviceUrl, ResolveHostAddressesAsync, cancellationToken);

    internal static async Task<GeoservicesServiceUrlValidationResult> ValidateAsync(
        string serviceUrl,
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

        if (await NetworkAddressValidator.IsDisallowedAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return GeoservicesServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return GeoservicesServiceUrlValidationResult.Success();
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

}
