// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

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

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            return GeoServerServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        if (await IsPrivateOrUnresolvableAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return GeoServerServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return GeoServerServiceUrlValidationResult.Success();
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    private static async Task<bool> IsPrivateOrUnresolvableAddressAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return IsPrivateOrReservedAddress(literalAddress);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (addresses.Length == 0)
        {
            return true;
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            if (bytes[0] == 0 ||
                bytes[0] == 10 ||
                (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) ||
                (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                bytes[0] >= 224)
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            if (address.Equals(IPAddress.IPv6None) ||
                address.Equals(IPAddress.IPv6Loopback) ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) ||
                (bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8))
            {
                return true;
            }
        }

        return false;
    }
}
