// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalWorkerLicensingTests
{
    [IntegrationTest]
    public async Task AddGdalWorker_WithNonPersistentRedis_RejectsBeforeRegisteringJobStores()
    {
        await using var redis = new ContainerBuilder()
            .WithImage("redis:7.2-alpine")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--appendonly", "no", "--save", "")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        await redis.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();

        var register = () => services.AddGdalWorker(configuration);

        register.Should().Throw<InvalidOperationException>().WithMessage("*RedisPersistenceDisabled*");
        using var provider = services.BuildServiceProvider();
        provider.GetService<IExecutionJobStore>().Should().BeNull();
        provider.GetService<IJobQueue>().Should().BeNull();
    }

    [IntegrationTest]
    public async Task AddGdalWorker_WithDurableRedis_RegistersLicensePolicyAndBothCloudSecretResolvers()
    {
        await using var redis = new ContainerBuilder()
            .WithImage("redis:7.2-alpine")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always",
                "--maxmemory-policy", "noeviction")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
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

        provider.GetRequiredService<RedisDurabilityAttestation>().EvictionPolicy.Should().Be("noeviction");
        provider.GetRequiredService<IExecutionJobStore>().Should().NotBeNull();
        provider.GetRequiredService<IJobQueue>().Should().NotBeNull();
        var policy = provider.GetRequiredService<ILicenseOperationPolicy>();
        policy.Should().BeAssignableTo<IHostedService>();
        policy.IsBlocked.Should().BeFalse();
        var resolvers = provider.GetServices<ILicenseContentSecretResolver>().ToArray();
        resolvers.Should().Contain(resolver => resolver.CanResolve("aws:secretsmanager:arn:aws:secretsmanager:us-east-1:000000000000:secret:synthetic"));
        resolvers.Should().Contain(resolver => resolver.CanResolve("azure:keyvault:https://synthetic.vault.azure.net/secrets/synthetic"));
    }
}
