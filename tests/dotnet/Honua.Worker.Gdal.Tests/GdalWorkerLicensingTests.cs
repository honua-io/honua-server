// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
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
    public async Task AssignedWorker_TerminalOperation_ExitsWithoutClaimingQueuedWork()
    {
        await using var redis = BuildRedis("yes", "always", "noeviction");
        await redis.StartAsync();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
            ["Licensing:Mode"] = "Disabled",
        });
        builder.Services.AddGdalWorker(builder.Configuration);
        var operationId = $"native-assigned-{Guid.NewGuid():N}";
        builder.Services.AddAssignedExecutionWorker(new ExecutionWorkerLaunch
        {
            OperationId = operationId,
            JobKind = ExecutionJobKind.Geoprocessing,
            RuntimeProfile = "native",
            ExecutionAttempt = 1,
        });
        var host = builder.Build();
        var store = host.Services.GetRequiredService<IExecutionJobStore>();
        var now = DateTimeOffset.UtcNow;
        var terminal = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "honua-kubernetes-job",
                WorkloadName = "native",
                RuntimeProfile = "native",
            },
        };
        await store.SetAsync(terminal);
        var unrelatedId = $"unrelated-{Guid.NewGuid():N}";
        await store.SetAsync(terminal with { OperationId = unrelatedId, Status = ExecutionJobStatus.Queued });
        using var observer = await ConnectionMultiplexer.ConnectAsync(
            $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}");
        await observer.GetDatabase().SortedSetAddAsync("controlplane:jobqueue:pending", unrelatedId, 0);

        host.Services.GetRequiredService<IJobQueue>().Should().BeOfType<AssignedExecutionJobQueue>();
        host.Services.GetServices<IHostedService>().Should().NotContain(service => service is JobReconciliationService);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exitCode = await host.RunExecutionWorkerAsync(timeout.Token);

        timeout.IsCancellationRequested.Should().BeFalse("the assigned worker must stop itself");
        exitCode.Should().Be(0);
        (await observer.GetDatabase().SortedSetScoreAsync("controlplane:jobqueue:pending", unrelatedId))
            .Should().Be(0);
        (await observer.GetDatabase().SortedSetScoreAsync("controlplane:jobqueue:claimed", unrelatedId))
            .Should().BeNull();
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

    /// <summary>
    /// Owner ruling (2026-10-10): the attestation is information. A connected Redis whose policy
    /// is not durable (or, on managed Redis, unreadable) still runs the worker; the outcome is
    /// logged, and the accepted-attestation evidence object is simply not published.
    /// </summary>
    [IntegrationTheory]
    [InlineData("no", "always", "noeviction")]
    [InlineData("yes", "no", "noeviction")]
    [InlineData("yes", "always", "allkeys-lru")]
    public async Task AddGdalWorker_NonDurableRedis_StillRegistersDurableJobs(
        string appendOnly, string appendFsync, string evictionPolicy)
    {
        await using var redis = BuildRedis(appendOnly, appendFsync, evictionPolicy);
        await redis.StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddGdalWorker(RedisConfiguration(redis));
        using var provider = services.BuildServiceProvider();
        using var connection = provider.GetRequiredService<IConnectionMultiplexer>();

        provider.GetService<RedisDurabilityAttestation>().Should().BeNull();
        provider.GetRequiredService<IExecutionJobStore>().Should().NotBeNull();
        provider.GetRequiredService<IJobQueue>().Should().NotBeNull();
    }

    [IntegrationTheory]
    [InlineData("no", "always", "noeviction", DurableJobSubstrateCause.RedisPersistenceDisabled)]
    [InlineData("yes", "no", "noeviction", DurableJobSubstrateCause.RedisWritePolicyUnsafe)]
    [InlineData("yes", "always", "allkeys-lru", DurableJobSubstrateCause.RedisEvictionPolicyUnsafe)]
    public async Task AddGdalWorker_RequireDurableStoreOnNonDurableRedis_RefusesBeforeRegisteringDurableJobs(
        string appendOnly, string appendFsync, string evictionPolicy, DurableJobSubstrateCause expectedCause)
    {
        await using var redis = BuildRedis(appendOnly, appendFsync, evictionPolicy);
        await redis.StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();

        var register = () => services.AddGdalWorker(RedisConfiguration(redis, requireDurableStore: true));

        register.Should().Throw<DurableJobSubstrateNotAttestedException>()
            .Which.Cause.Should().Be(expectedCause);
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IJobQueue));
    }

    private static IConfiguration RedisConfiguration(IContainer redis, bool requireDurableStore = false) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
                ["Jobs:RequireDurableStore"] = requireDurableStore ? "true" : null,
            }).Build();

    private static IContainer BuildRedis(string appendOnly, string appendFsync, string evictionPolicy) =>
        new ContainerBuilder()
            .WithImage("redis:7.2-alpine")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--appendonly", appendOnly, "--appendfsync", appendFsync,
                "--save", "", "--maxmemory-policy", evictionPolicy)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
}
