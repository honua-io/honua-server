// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.ArcGisRest.Features.FeatureStore.Services;

namespace Honua.ArcGisRest.Tests;

/// <summary>
/// Regression coverage for #2012 (DNS-rebinding SSRF / TOCTOU): the federated
/// outbound guard must re-resolve and re-validate the target host's IP at
/// connect time, so a rebinding A-record that flips from a public address at
/// registration to a private/metadata address at send time is rejected. The
/// guard exposes an injectable resolver so the re-check is unit-testable without
/// real DNS.
/// </summary>
public sealed class ArcGisRestOutboundGuardTests
{
    [Theory]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("10.0.0.5")]        // RFC1918 private
    [InlineData("172.16.0.1")]      // RFC1918 private
    [InlineData("192.168.1.1")]     // RFC1918 private
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("fc00::1")]         // IPv6 ULA
    [InlineData("fe80::1")]         // IPv6 link-local
    public void IsPrivateOrReservedAddress_BlockedRanges_ReturnsTrue(string address)
    {
        Assert.True(ArcGisRestOutboundGuard.IsPrivateOrReservedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")] // example.com
    public void IsPrivateOrReservedAddress_PublicAddresses_ReturnsFalse(string address)
    {
        Assert.False(ArcGisRestOutboundGuard.IsPrivateOrReservedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_PublicResolution_ReturnsAddresses()
    {
        var resolved = await ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
            "services.arcgis.com",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.114.10") }),
            CancellationToken.None);

        Assert.Single(resolved);
        Assert.Equal(IPAddress.Parse("203.0.114.10"), resolved[0]);
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_RebindToMetadataAddress_Throws()
    {
        // Simulates the TOCTOU window: the host validated as public at registration
        // now resolves to the cloud-metadata address at connect time. The connect-time
        // re-check (this very method) must reject it.
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
                "rebind.attacker.example",
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
                CancellationToken.None));

        Assert.Equal(ArcGisRestOutboundGuard.DisallowedNetworkAddressMessage, exception.Message);
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_RebindWithMixedAddresses_RejectsWhenAnyIsBlocked()
    {
        // A single blocked address among otherwise-public results must fail the whole
        // resolution; an attacker cannot smuggle a private target alongside a public one.
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
                "rebind.attacker.example",
                (_, _) => Task.FromResult(new[]
                {
                    IPAddress.Parse("8.8.8.8"),
                    IPAddress.Parse("10.10.10.10"),
                }),
                CancellationToken.None));

        Assert.Equal(ArcGisRestOutboundGuard.DisallowedNetworkAddressMessage, exception.Message);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("svc.localhost")]
    public async Task ResolveAllowedAddressesAsync_LocalhostHostName_Throws(string host)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
                host,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_PrivateIpLiteral_ThrowsWithoutResolving()
    {
        var resolverInvoked = false;

        await Assert.ThrowsAsync<ArgumentException>(
            () => ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
                "10.1.2.3",
                (_, _) =>
                {
                    resolverInvoked = true;
                    return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
                },
                CancellationToken.None));

        Assert.False(resolverInvoked, "An IP literal must be classified directly, never resolved.");
    }

    [Fact]
    public async Task ResolveAllowedAddressesAsync_EmptyResolution_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => ArcGisRestOutboundGuard.ResolveAllowedAddressesAsync(
                "nxdomain.example",
                (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
                CancellationToken.None));
    }
}
