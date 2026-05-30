// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(TestProtocols.Infrastructure)]
public sealed class RedisConnectionSelectorTests
{
    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void SelectInfrastructureConnectionString_WithCacheEntitlement_ReturnsConfiguredConnection()
    {
        var result = RedisConnectionSelector.SelectInfrastructureConnectionString(
            "localhost:6379",
            redisCacheEntitled: true,
            requiresDurableDistributedEvents: false);

        result.Should().Be("localhost:6379");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void SelectInfrastructureConnectionString_WithDurableEventsRequired_ReturnsConfiguredConnection()
    {
        var result = RedisConnectionSelector.SelectInfrastructureConnectionString(
            "redis:6379",
            redisCacheEntitled: false,
            requiresDurableDistributedEvents: true);

        result.Should().Be("redis:6379");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void SelectInfrastructureConnectionString_WithoutCacheOrDurableRequirement_ReturnsNull()
    {
        var result = RedisConnectionSelector.SelectInfrastructureConnectionString(
            "redis:6379",
            redisCacheEntitled: false,
            requiresDurableDistributedEvents: false);

        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void SelectInfrastructureConnectionString_WithoutConfiguredRedis_ReturnsNull()
    {
        var result = RedisConnectionSelector.SelectInfrastructureConnectionString(
            null,
            redisCacheEntitled: true,
            requiresDurableDistributedEvents: true);

        result.Should().BeNull();
    }
}
