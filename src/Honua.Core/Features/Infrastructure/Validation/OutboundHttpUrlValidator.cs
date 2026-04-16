// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Outcome of an <see cref="OutboundHttpUrlValidator"/> check, exposing the parsed URI on success
/// or a human-readable error suffix on failure.
/// </summary>
/// <param name="IsValid">Whether the URL passed all validation rules.</param>
/// <param name="Uri">The parsed URI when <paramref name="IsValid"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
/// <param name="ErrorMessage">A short, sentence-fragment error suffix (e.g., "must be a valid HTTPS URL.") suitable for appending to a property name.</param>
public readonly record struct OutboundHttpUrlValidationResult(bool IsValid, Uri? Uri, string? ErrorMessage)
{
    /// <summary>
    /// Creates a successful validation result for the supplied parsed URI.
    /// </summary>
    public static OutboundHttpUrlValidationResult Success(Uri uri) => new(true, uri, null);

    /// <summary>
    /// Creates a failed validation result with the supplied error message.
    /// </summary>
    public static OutboundHttpUrlValidationResult Failure(string message) => new(false, null, message);
}

/// <summary>
/// Validates outbound HTTP endpoints and blocks local or private destinations.
/// </summary>
public static class OutboundHttpUrlValidator
{
    private const string InvalidHttpsUrlMessage = "must be a valid HTTPS URL.";
    private const string EmbeddedCredentialsMessage = "must not include embedded credentials.";
    private const string DisallowedAddressMessage =
        "resolves to a private, loopback, or unresolvable network address, which is not allowed.";

    /// <summary>
    /// Asynchronously validates an outbound HTTPS URL, resolving the host and rejecting any
    /// addresses in private, loopback, link-local, multicast, or otherwise reserved ranges.
    /// </summary>
    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        CancellationToken cancellationToken = default)
        => ValidateAsync(url, ResolveHostAddressesAsync, cancellationToken);

    /// <summary>
    /// Synchronously validates an outbound HTTPS URL using a blocking DNS lookup, rejecting
    /// hostnames that resolve to private, loopback, or otherwise reserved addresses. Use this
    /// from synchronous configuration validators where <see cref="ValidateAsync(string, CancellationToken)"/>
    /// is not available.
    /// </summary>
    public static OutboundHttpUrlValidationResult ValidateConfiguration(string url)
        => ValidateConfiguration(url, ResolveHostAddresses);

    internal static async Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (!TryValidateBaseUri(url, out var failure, out var uri))
        {
            return failure;
        }

        if (await IsPrivateOrUnresolvableAddressAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false))
        {
            return OutboundHttpUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    internal static OutboundHttpUrlValidationResult ValidateConfiguration(
        string url,
        Func<string, IPAddress[]> hostAddressResolver)
    {
        if (!TryValidateBaseUri(url, out var failure, out var uri))
        {
            return failure;
        }

        if (IsPrivateOrUnresolvableAddress(uri, hostAddressResolver))
        {
            return OutboundHttpUrlValidationResult.Failure(DisallowedAddressMessage);
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    private static bool TryValidateBaseUri(
        string url,
        out OutboundHttpUrlValidationResult failure,
        out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            failure = OutboundHttpUrlValidationResult.Failure(InvalidHttpsUrlMessage);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            failure = OutboundHttpUrlValidationResult.Failure(EmbeddedCredentialsMessage);
            return false;
        }

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            failure = OutboundHttpUrlValidationResult.Failure(DisallowedAddressMessage);
            return false;
        }

        failure = default;
        return true;
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    private static IPAddress[] ResolveHostAddresses(string host)
        => Dns.GetHostAddresses(host);

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

        return ContainsPrivateOrReservedAddress(addresses);
    }

    private static bool IsPrivateOrUnresolvableAddress(
        Uri uri,
        Func<string, IPAddress[]> hostAddressResolver)
    {
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return IsPrivateOrReservedAddress(literalAddress);
        }

        IPAddress[] addresses;
        try
        {
            addresses = hostAddressResolver(uri.DnsSafeHost);
        }
        catch (SocketException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }

        return ContainsPrivateOrReservedAddress(addresses);
    }

    private static bool ContainsPrivateOrReservedAddress(IPAddress[] addresses)
    {
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
            return bytes[0] == 0 ||
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
                   bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6None) ||
                   address.Equals(IPAddress.IPv6Loopback) ||
                   (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                   (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) ||
                   (bytes[0] & 0xfe) == 0xfc ||
                   (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        return false;
    }
}
