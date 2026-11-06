// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

namespace Honua.Core.Features.Infrastructure.Validation;

public enum OutboundHttpUrlFailureReason
{
    // An unclassified failure is permanent and is never reported as retryable.
    None = 0,

    InvalidUrl,

    EmbeddedCredentials,

    // A private or reserved address is a permanent failure; retrying cannot help.
    DisallowedAddress,

    HostResolutionUnavailable
}

public readonly record struct OutboundHttpUrlValidationResult(bool IsValid, Uri? Uri, string? ErrorMessage)
{
    // The classification stays off the positional constructor because this package is published and a new positional member would break existing binaries.
    public OutboundHttpUrlFailureReason FailureReason { get; init; }

    // A resolution failure still blocks the destination, and the failure is indeterminate rather than permanent.
    public bool IsHostResolutionUnavailable
        => FailureReason == OutboundHttpUrlFailureReason.HostResolutionUnavailable;

    public static OutboundHttpUrlValidationResult Success(Uri uri) => new(true, uri, null);

    public static OutboundHttpUrlValidationResult Failure(string message) => new(false, null, message);

    public static OutboundHttpUrlValidationResult Failure(string message, OutboundHttpUrlFailureReason reason)
        => new(false, null, message) { FailureReason = reason };
}

public static class OutboundHttpUrlValidator
{
    private const string InvalidHttpsUrlMessage = "must be a valid HTTPS URL.";
    private const string EmbeddedCredentialsMessage = "must not include embedded credentials.";
    private const string DisallowedAddressMessage =
        "resolves to a private, loopback, or unresolvable network address, which is not allowed.";
    private const string HostResolutionUnavailableMessage =
        "could not be verified because host name resolution is currently unavailable; the destination was not contacted.";

    private enum HostVettingOutcome
    {
        Allowed,
        Disallowed,
        ResolutionUnavailable
    }

    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        CancellationToken cancellationToken = default)
        => ValidateAsync(url, allowPrivateNetworks: false, cancellationToken);

    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken = default)
        => ValidateAsync(url, ResolveHostAddressesAsync, allowPrivateNetworks, cancellationToken);

    public static OutboundHttpUrlValidationResult ValidateConfiguration(string url)
        => ValidateConfiguration(url, allowPrivateNetworks: false);

    public static OutboundHttpUrlValidationResult ValidateConfiguration(string url, bool allowPrivateNetworks)
        => ValidateConfiguration(url, ResolveHostAddresses, allowPrivateNetworks);

    public static Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken = default)
        => ValidateAsync(url, hostAddressResolver, allowPrivateNetworks: false, cancellationToken);

    internal static async Task<OutboundHttpUrlValidationResult> ValidateAsync(
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken)
    {
        if (!TryValidateBaseUri(url, allowPrivateNetworks, out var failure, out var uri))
        {
            return failure;
        }

        if (!allowPrivateNetworks)
        {
            var outcome = await VetHostAsync(uri, hostAddressResolver, cancellationToken).ConfigureAwait(false);
            if (outcome != HostVettingOutcome.Allowed)
            {
                return CreateHostFailure(outcome);
            }
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    internal static OutboundHttpUrlValidationResult ValidateConfiguration(
        string url,
        Func<string, IPAddress[]> hostAddressResolver)
        => ValidateConfiguration(url, hostAddressResolver, allowPrivateNetworks: false);

    internal static OutboundHttpUrlValidationResult ValidateConfiguration(
        string url,
        Func<string, IPAddress[]> hostAddressResolver,
        bool allowPrivateNetworks)
    {
        if (!TryValidateBaseUri(url, allowPrivateNetworks, out var failure, out var uri))
        {
            return failure;
        }

        if (!allowPrivateNetworks)
        {
            var outcome = VetHost(uri, hostAddressResolver);
            if (outcome != HostVettingOutcome.Allowed)
            {
                return CreateHostFailure(outcome);
            }
        }

        return OutboundHttpUrlValidationResult.Success(uri);
    }

    private static OutboundHttpUrlValidationResult CreateHostFailure(HostVettingOutcome outcome)
        => outcome == HostVettingOutcome.ResolutionUnavailable
            ? OutboundHttpUrlValidationResult.Failure(
                HostResolutionUnavailableMessage,
                OutboundHttpUrlFailureReason.HostResolutionUnavailable)
            : OutboundHttpUrlValidationResult.Failure(
                DisallowedAddressMessage,
                OutboundHttpUrlFailureReason.DisallowedAddress);

    private static bool TryValidateBaseUri(
        string url,
        bool allowPrivateNetworks,
        out OutboundHttpUrlValidationResult failure,
        out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) ||
            !IsAllowedScheme(uri, allowPrivateNetworks))
        {
            failure = OutboundHttpUrlValidationResult.Failure(
                InvalidHttpsUrlMessage,
                OutboundHttpUrlFailureReason.InvalidUrl);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            failure = OutboundHttpUrlValidationResult.Failure(
                EmbeddedCredentialsMessage,
                OutboundHttpUrlFailureReason.EmbeddedCredentials);
            return false;
        }

        // Loopback/localhost is only rejected under the default (strict) posture. The private-network
        // opt-in deliberately permits it for trusted operator-configured destinations.
        if (!allowPrivateNetworks && (uri.IsLoopback || IsLocalhostHostName(uri.Host)))
        {
            failure = OutboundHttpUrlValidationResult.Failure(
                DisallowedAddressMessage,
                OutboundHttpUrlFailureReason.DisallowedAddress);
            return false;
        }

        failure = default;
        return true;
    }

    private static bool IsAllowedScheme(Uri uri, bool allowPrivateNetworks)
        => uri.Scheme == Uri.UriSchemeHttps ||
           (allowPrivateNetworks && uri.Scheme == Uri.UriSchemeHttp);

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    private static IPAddress[] ResolveHostAddresses(string host)
        => Dns.GetHostAddresses(host);

    // Anything other than a conclusively public address blocks the destination; a resolver failure is indeterminate, and a malformed host name is permanent.
    private static async Task<HostVettingOutcome> VetHostAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return ToOutcome(IsPrivateOrReservedAddress(literalAddress));
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return HostVettingOutcome.ResolutionUnavailable;
        }
        catch (ArgumentException)
        {
            return HostVettingOutcome.Disallowed;
        }

        return ToOutcome(ContainsPrivateOrReservedAddress(addresses));
    }

    private static HostVettingOutcome VetHost(
        Uri uri,
        Func<string, IPAddress[]> hostAddressResolver)
    {
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return ToOutcome(IsPrivateOrReservedAddress(literalAddress));
        }

        IPAddress[] addresses;
        try
        {
            addresses = hostAddressResolver(uri.DnsSafeHost);
        }
        catch (SocketException)
        {
            return HostVettingOutcome.ResolutionUnavailable;
        }
        catch (ArgumentException)
        {
            return HostVettingOutcome.Disallowed;
        }

        return ToOutcome(ContainsPrivateOrReservedAddress(addresses));
    }

    private static HostVettingOutcome ToOutcome(bool isPrivateOrReserved)
        => isPrivateOrReserved ? HostVettingOutcome.Disallowed : HostVettingOutcome.Allowed;

    private static bool ContainsPrivateOrReservedAddress(IPAddress[] addresses)
        => addresses.Length == 0 || addresses.Any(IsPrivateOrReservedAddress);

    public static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    public static bool IsPrivateOrReservedAddress(IPAddress address)
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
            if (address.Equals(IPAddress.IPv6None) ||
                address.Equals(IPAddress.IPv6Loopback) ||
                bytes[0] == 0xff ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
                (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) ||
                (bytes[0] & 0xfe) == 0xfc ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8))
            {
                return true;
            }

            // Teredo (2001::/32) tunnels an obfuscated IPv4 endpoint that cannot be vetted
            // reliably; treat it as reserved for outbound destinations.
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return true;
            }

            // 6to4 (2002::/16) embeds an IPv4 address; vet it so 2002:<private-v4>:: cannot
            // smuggle a reserved destination past the check.
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsPrivateOrReservedAddress(new IPAddress(bytes.AsSpan(2, 4)));
            }

            // NAT64 (64:ff9b::/32, covering the well-known 64:ff9b::/96 and local-use
            // 64:ff9b:1::/48 prefixes) embeds the translated IPv4 in the low 32 bits; vet it.
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
            {
                return IsPrivateOrReservedAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }

            return false;
        }

        return false;
    }
}
