// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ClientAddressPartitionTests
{
    [UnitTest]
    public void From_Ipv6AddressesInOnePrefix_ShareAPartition()
    {
        ClientAddressPartition.From(IPAddress.Parse("2001:db8:1:2:aaaa::1"))
            .Should().Be(ClientAddressPartition.From(IPAddress.Parse("2001:db8:1:2:bbbb::2")));
    }

    [UnitTest]
    public void From_Ipv4MappedAddress_ReturnsThePlainIpv4Address()
    {
        ClientAddressPartition.From(IPAddress.Parse("::ffff:198.51.100.7")).Should().Be("198.51.100.7");
    }

    [UnitTest]
    public void From_NoAddress_ReturnsNull()
    {
        ClientAddressPartition.From(null).Should().BeNull();
    }
}
