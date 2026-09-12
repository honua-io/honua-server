// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
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
    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("not-a-signed-license")]
    public async Task AddGdalWorker_DisabledProduction_StartsWithoutLoadingLegacyLicense(string? licenseContent)
    {
        await using var redis = BuildRedis("yes", "always", "noeviction");
        await redis.StartAsync();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
            ["Licensing:Mode"] = "Disabled",
            ["Licensing:Edition"] = "Enterprise",
            ["Licensing:LicensePath"] = "/nonexistent/disabled-worker/license.json",
            ["Licensing:LicenseContent"] = licenseContent,
            ["Licensing:LicenseContentSecretRef"] = null
        });
        builder.Services.AddGdalWorker(builder.Configuration);
        using var host = builder.Build();
        using var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        await host.StartAsync();
        try
        {
            host.Services.GetRequiredService<IHostEnvironment>().IsProduction().Should().BeTrue();
            var policy = host.Services.GetRequiredService<ILicenseOperationPolicy>();
            policy.IsBlocked.Should().BeFalse();
            policy.OperationCancellation.CanBeCanceled.Should().BeFalse();
            host.Services.GetServices<IHostedService>().Should().NotContain(service => ReferenceEquals(service, policy));
            host.Services.GetServices<ILicenseContentSecretResolver>().Should().BeEmpty();
            var snapshot = host.Services.GetRequiredService<ILicenseEntitlementService>().GetSnapshot();
            snapshot.Mode.Should().Be(LicenseMode.Disabled);
            snapshot.Entitlements.Should().HaveCount(FeatureCatalog.All.Count).And.OnlyContain(entitlement => entitlement.IsActive);
            var capacity = await host.Services.GetRequiredService<ILicenseCapacityMeter>().GetCapacityStateAsync();
            capacity.MeteringEnabled.Should().BeFalse();
            capacity.LiveInstanceCount.Should().Be(0);
        }
        finally
        {
            await host.StopAsync();
        }
    }

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
