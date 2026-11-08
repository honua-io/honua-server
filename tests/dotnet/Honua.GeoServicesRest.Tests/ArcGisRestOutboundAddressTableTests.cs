// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Db.GeoServicesRest.Features.FeatureStore.Services;
using Honua.Tests.OutboundSecurity;
using Xunit;

namespace Honua.Db.GeoServicesRest.Tests;

public sealed class ArcGisRestOutboundAddressTableTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(OutboundAddressCases.Disallowed), MemberType = typeof(OutboundAddressCases))]
    public async Task ResolveAllowedAddressesAsync_HostResolvingToDisallowedRange_Throws(string address, string range)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => GeoServicesRestOutboundGuard.ResolveAllowedAddressesAsync("arcgis.example.com", Resolve(address), CancellationToken.None));

        Assert.True(exception.Message == GeoServicesRestOutboundGuard.DisallowedNetworkAddressMessage, range);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [MemberData(nameof(OutboundAddressCases.Allowed), MemberType = typeof(OutboundAddressCases))]
    public async Task ResolveAllowedAddressesAsync_HostResolvingToPublicAddress_ReturnsIt(string address, string range)
    {
        var resolved = await GeoServicesRestOutboundGuard.ResolveAllowedAddressesAsync(
            "arcgis.example.com", Resolve(address), CancellationToken.None);

        Assert.True(resolved.SequenceEqual([IPAddress.Parse(address)]), range);
    }

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolve(string address)
        => (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) });
}
