// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Guards the startup Redis-cache entitlement gate
/// (<see cref="StartupConfigurationHelpers.IsRedisCacheEntitledAsync"/>). The gate decides
/// whether the host wires the <c>IConnectionMultiplexer</c> that the durable job store depends
/// on. Before honua-server#1787 it read a bootstrap license snapshot that ignored
/// <c>Licensing:DevGrantEdition</c>, so a Development + <c>DevGrantEdition=Pro</c> stack with
/// Redis reachable still had no store and GPServer <c>/execute</c>/<c>/submitJob</c> returned 503.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class RedisCacheEntitlementGateTests
{
    private const string RedisConnectionString = "localhost:6379";

    [UnitTest]
    public async Task IsRedisCacheEntitled_DevGrantPro_InDevelopment_EntitlesRedis()
    {
        // Reproduces honua-server#1787: no signed license, only Licensing:DevGrantEdition=Pro.
        var configuration = BuildConfiguration(
            redisConnectionString: RedisConnectionString,
            devGrantEdition: "Pro");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(configuration, environment);

        entitled.Should().BeTrue(
            "DevGrantEdition=Pro grants caching.redis at runtime, so the startup gate must agree " +
            "and wire the durable job store the GPServer needs");
    }

    [UnitTest]
    public async Task IsRedisCacheEntitled_NoLicenseNoDevGrant_DoesNotEntitleRedis()
    {
        var configuration = BuildConfiguration(
            redisConnectionString: RedisConnectionString,
            devGrantEdition: null);
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse(
            "without a signed license or a dev grant the Community snapshot does not include caching.redis");
    }

    [UnitTest]
    public async Task IsRedisCacheEntitled_DevGrantPro_InProduction_DoesNotEntitleRedis()
    {
        // Fail-closed: the dev-only override must never relax a startup gate in Production.
        var configuration = BuildConfiguration(
            redisConnectionString: RedisConnectionString,
            devGrantEdition: "Pro");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };

        var entitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse(
            "DevGrantEdition is a test/dev-only override and must be ignored by the gate in Production");
    }

    [UnitTest]
    public async Task IsRedisCacheEntitled_DevGrantCommunity_DoesNotEntitleRedis()
    {
        // caching.redis is a Pro entitlement; a Community dev grant must not unlock it.
        var configuration = BuildConfiguration(
            redisConnectionString: RedisConnectionString,
            devGrantEdition: "Community");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse(
            "caching.redis requires Pro; a Community dev grant does not reach that edition");
    }

    [UnitTest]
    public async Task IsRedisCacheEntitled_NoRedisConnectionString_ReturnsFalse()
    {
        var configuration = BuildConfiguration(
            redisConnectionString: null,
            devGrantEdition: "Pro");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse("there is nothing to connect to without a Redis connection string");
    }

    private static IConfiguration BuildConfiguration(string? redisConnectionString, string? devGrantEdition)
    {
        var settings = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            settings["ConnectionStrings:redis"] = redisConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(devGrantEdition))
        {
            settings["Licensing:DevGrantEdition"] = devGrantEdition;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Honua.Server.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
