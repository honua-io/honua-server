// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Import.FileImport;
using Honua.TestKit.Attributes;
using Honua.Tests.OutboundSecurity;

namespace Honua.Server.Tests.Import;

public sealed class NetworkAddressValidatorAddressTableTests
{
    private static readonly Uri ServiceUri = new("https://arcgis.example.com/arcgis/rest/services/Test/FeatureServer");

    [UnitTheory]
    [MemberData(nameof(OutboundAddressCases.Disallowed), MemberType = typeof(OutboundAddressCases))]
    public async Task ValidateAndResolveAsync_HostResolvingToDisallowedRange_IsDisallowed(string address, string range)
    {
        var (isDisallowed, _) = await NetworkAddressValidator.ValidateAndResolveAsync(
            ServiceUri, Resolve(address), CancellationToken.None);

        isDisallowed.Should().BeTrue(range);
    }

    [UnitTheory]
    [MemberData(nameof(OutboundAddressCases.Allowed), MemberType = typeof(OutboundAddressCases))]
    public async Task ValidateAndResolveAsync_HostResolvingToPublicAddress_IsAllowed(string address, string range)
    {
        var (isDisallowed, _) = await NetworkAddressValidator.ValidateAndResolveAsync(
            ServiceUri, Resolve(address), CancellationToken.None);

        isDisallowed.Should().BeFalse(range);
    }

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolve(string address)
        => (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) });
}
