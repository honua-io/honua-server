// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class GdalWorkerLicensingTests
{
    [IntegrationTest]
    public async Task AddGdalWorker_AttestsRedisAndRegistersLicensePolicyAndBothCloudSecretResolvers()
    {
        await using var redis = BuildRedis("yes", "always", "noeviction");
        await redis.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGdalWorker(configuration);
        using var provider = services.BuildServiceProvider();
        using var connection = provider.GetRequiredService<IConnectionMultiplexer>();

        provider.GetRequiredService<RedisDurabilityAttestation>().AcknowledgedWritePolicy.Should().Be("appendfsync=always");
        provider.GetRequiredService<IExecutionJobStore>().Should().NotBeNull();
        provider.GetRequiredService<IJobQueue>().Should().NotBeNull();

        var policy = provider.GetRequiredService<ILicenseOperationPolicy>();
        policy.Should().BeAssignableTo<IHostedService>();
        policy.IsBlocked.Should().BeFalse();
        var resolvers = provider.GetServices<ILicenseContentSecretResolver>().ToArray();
        resolvers.Should().Contain(resolver => resolver.CanResolve("aws:secretsmanager:arn:aws:secretsmanager:us-east-1:000000000000:secret:synthetic"));
        resolvers.Should().Contain(resolver => resolver.CanResolve("azure:keyvault:https://synthetic.vault.azure.net/secrets/synthetic"));
    }

    [IntegrationTheory]
    [InlineData("no", "always", "noeviction", DurableJobSubstrateCause.RedisPersistenceDisabled)]
    [InlineData("yes", "no", "noeviction", DurableJobSubstrateCause.RedisWritePolicyUnsafe)]
    [InlineData("yes", "always", "allkeys-lru", DurableJobSubstrateCause.RedisEvictionPolicyUnsafe)]
    public async Task AddGdalWorker_UnsafeRedis_RejectsBeforeRegisteringDurableJobs(
        string appendOnly, string appendFsync, string evictionPolicy, DurableJobSubstrateCause expectedCause)
    {
        await using var redis = BuildRedis(appendOnly, appendFsync, evictionPolicy);
        await redis.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();

        var register = () => services.AddGdalWorker(configuration);

        register.Should().Throw<InvalidOperationException>().WithMessage($"*rejected ({expectedCause})*");
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IJobQueue));
    }

    private static IContainer BuildRedis(string appendOnly, string appendFsync, string evictionPolicy) =>
        new ContainerBuilder()
            .WithImage("redis:7.2-alpine")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--appendonly", appendOnly, "--appendfsync", appendFsync,
                "--save", "", "--maxmemory-policy", evictionPolicy)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
}
