// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Realtime;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Server.Features.Streaming;
using Honua.Server.Startup;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

[Protocol(TestProtocols.Health)]
public sealed class FeatureEventsAndStreamingRegistrationTests
{
    [UnitTest]
    public void ResolveSessionManager_WithScopedMetadataProvider_DoesNotCaptureRootScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IMetadataV2GraphProvider>());
        services.AddHonuaFeatureEventsAndStreaming(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        var manager = provider.GetRequiredService<FeatureStreamSessionManager>();

        manager.Should().NotBeNull();
    }

    [UnitTest]
    public async Task ResolveEventStore_AnyHost_IsTheDurableChangeLogStore()
    {
        await using var provider = BuildServices(new ConfigurationBuilder().Build());

        var store = provider.GetRequiredService<IFeatureChangeEventStore>();
        var health = provider.GetRequiredService<IFeatureChangeEventStoreHealth>();

        store.Should().BeOfType<PostgresFeatureChangeEventStore>();
        health.Should().BeSameAs(store);
        health.CanPersistEvents.Should().BeTrue();
        health.IsUsingInMemoryFallback.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolvePublisher_AnyHost_IsTheStreamPublisher()
    {
        await using var provider = BuildServices(new ConfigurationBuilder().Build());

        provider.GetRequiredService<IFeatureChangeEventPublisher>().Should().BeOfType<FeatureStreamPublisher>();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_ChangeLogStore_ReturnsReady()
    {
        await using var provider = BuildServices(new ConfigurationBuilder().Build());
        var health = provider.GetRequiredService<IFeatureChangeEventStoreHealth>();

        var result = await CreateReadinessService(health).CheckReadinessAsync();

        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
    }

    [UnitTest]
    public void AddHonuaFeatureEventsAndStreaming_HostWithDatabasePollingLoops_HostsTheRealtimeRelay()
    {
        var services = new ServiceCollection();

        services.AddHonuaFeatureEventsAndStreaming(new ConfigurationBuilder().Build());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory != null
            && descriptor.ImplementationFactory.Method.DeclaringType == typeof(FeatureStreamRealtimeRelay));
    }

    [UnitTest]
    public void AddHonuaFeatureEventsAndStreaming_EventHandlerHost_DoesNotHostTheRealtimeRelay()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = "scheduled-tick"
            })
            .Build();

        services.AddHonuaFeatureEventsAndStreaming(configuration);

        services.Should().NotContain(descriptor => descriptor.ImplementationFactory != null
            && descriptor.ImplementationFactory.Method.DeclaringType == typeof(FeatureStreamRealtimeRelay));
    }

    [UnitTest]
    public async Task ResolveHostedServices_WithoutDatabaseAndDisabledWebhooks_DoesNotRequireDatabase()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHonuaFeatureEventsAndStreaming(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        hostedServices.Should().ContainSingle(service => service is FeatureStreamRealtimeRelay);
        hostedServices.Should().NotContain(service => service is FeatureChangeWebhookDispatcher);
    }

    private static ServiceProvider BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new JobStoreDatabase(NpgsqlDataSource.Create("Host=localhost;Database=unused")));
        services.AddSingleton(Options.Create(new RealtimeOptions()));
        services.AddSingleton<IRealtimeWakeSignal, NoWakeSignal>();
        services.AddHonuaFeatureEventsAndStreaming(configuration);
        return services.BuildServiceProvider();
    }

    private static ReadinessCheckService CreateReadinessService(IFeatureChangeEventStoreHealth health)
    {
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();
        return new ReadinessCheckService(
            new MockHealthyDatabaseChecker(),
            migrationState,
            new MockLogger<ReadinessCheckService>(),
            cacheHealthChecker: null,
            featureChangeEventStoreHealth: health);
    }

    private sealed class NoWakeSignal : IRealtimeWakeSignal
    {
        public void Wake()
        {
        }
    }
}
