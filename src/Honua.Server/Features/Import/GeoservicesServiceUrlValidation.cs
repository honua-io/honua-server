// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

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

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            return GeoservicesServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        if (await IsPrivateOrUnresolvableAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return GeoservicesServiceUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return GeoservicesServiceUrlValidationResult.Success();
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
            // Fail closed if DNS resolution fails.
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

        // Map IPv6-mapped IPv4 addresses to their IPv4 equivalent for consistent checking.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 0.0.0.0/8 (this network)
            if (bytes[0] == 0)
            {
                return true;
            }

            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return true;
            }

            // 100.64.0.0/10 (carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }

            // 127.0.0.0/8 (loopback)
            if (bytes[0] == 127)
            {
                return true;
            }

            // 169.254.0.0/16 (link-local, includes cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            // 192.0.0.0/24 (IETF protocol assignments)
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            {
                return true;
            }

            // 192.0.2.0/24 (TEST-NET-1 documentation)
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            {
                return true;
            }

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            // 198.18.0.0/15 (benchmarking)
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }

            // 198.51.100.0/24 (TEST-NET-2 documentation)
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            {
                return true;
            }

            // 203.0.113.0/24 (TEST-NET-3 documentation)
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            {
                return true;
            }

            // 224.0.0.0/4 and 240.0.0.0/4 (multicast/reserved)
            if (bytes[0] >= 224)
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            // ::/128 (unspecified)
            if (address.Equals(IPAddress.IPv6None))
            {
                return true;
            }

            // ::1/128 (loopback)
            if (address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }

            // fe80::/10 (IPv6 link-local)
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return true;
            }

            // fec0::/10 (deprecated site-local)
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
            {
                return true;
            }

            // fc00::/7 (unique local address)
            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            // 2001:db8::/32 (documentation)
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            {
                return true;
            }

            // ff00::/8 (multicast)
            if (bytes[0] == 0xff)
            {
                return true;
            }
        }

        return false;
    }
}
