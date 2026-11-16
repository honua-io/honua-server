// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.Server.Features.Collaboration.Operations;
using Honua.Server.Features.Realtime;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed class CollaborationRealtimeRelayTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transport_WithoutDatabase_DoesNotResolveDatabaseBackedFanout(bool initiallyConfigured)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<ISavedMapCollaborationAuthorizer>());
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=honua;Username=honua");
        if (initiallyConfigured)
        {
            services.AddSingleton(dataSource);
        }
        services.AddRealtimeFanout(new ConfigurationBuilder().Build(), hostsDatabasePollingLoops: true);
        services.AddCollaborationSessionTransport();
        services.RemoveAll<NpgsqlDataSource>();
        await using var container = services.BuildServiceProvider();

        var realtime = container.GetRequiredService<CollaborationSessionRealtimeServices>();
        realtime.Provider.Should().BeNull();
        realtime.Relay.Should().BeNull();
        container.GetRequiredService<CollaborationCapabilitySource>().Current.Operations.Should().BeTrue();
        container.GetRequiredService<SavedMapOperationAppendCoordinator>().CanAcceptEdits.Should().BeTrue();
        var hosted = container.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }
        foreach (var service in hosted.Reverse())
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task Reset_ResumesFromSuppliedHead_WhenWindowAdvancesDuringRecovery()
    {
        var after = await ObserveReconnectAsync([new RealtimeDelivery.Reset(RealtimeResetReasons.SlowConsumer, 12)]);
        after.Should().Be(12, "operations after the reset boundary must remain deliverable");
    }

    [UnitTest]
    public async Task Exception_ResumesAfterLastSuccessfulDelivery()
    {
        var after = await ObserveReconnectAsync([Operation(11, 1), Operation(12, 2)]);
        after.Should().Be(11, "a later delivery failure must not replay completed deliveries");
    }

    private static async Task<long?> ObserveReconnectAsync(IReadOnlyList<RealtimeDelivery> deliveries)
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var subscriptions = 0;
        var reconnect = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        provider.GetWindowAsync(Arg.Any<RealtimeFamily>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new RealtimeLogWindow(call.Arg<RealtimeFamily>(), 1, subscriptions == 0 ? 10 : 13)));
#pragma warning restore CA2012
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(call =>
        {
            var request = call.Arg<RealtimeSubscriptionRequest>();
            if (request.Family == RealtimeFamily.CollabOps)
            {
                if (Interlocked.Increment(ref subscriptions) == 1)
                {
                    foreach (var delivery in deliveries)
                    {
                        request.Writer.TryWrite(delivery);
                    }
                }
                else
                {
                    reconnect.TrySetResult(request.After);
                }
            }
            return Substitute.For<IRealtimeSubscription>();
        });
        await using var container = CreateContainer();
        using var relay = new CollaborationRealtimeRelay(provider, container, NullLogger<CollaborationRealtimeRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        try
        {
            return await reconnect.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider CreateContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CreateRepository());
        services.AddSingleton(new InMemoryCollaborationSessionService(
            Substitute.For<ISavedMapCollaborationAuthorizer>(), new SystemCollaborationSessionClock()));
        return services.BuildServiceProvider();
    }

    private static ISavedMapOperationLogRepository CreateRepository()
    {
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        var empty = new InMemorySavedMapOperationLogRepository(new MvpSavedMapOperationConflictPolicy());
        repository.ReplayAsync(Arg.Any<SavedMapId>(), Arg.Any<SavedMapOperationCursor>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<SavedMapOperationCursor>().Value == 1
                ? Task.FromException<SavedMapOperationReplayResult>(new IOException("Transient replay failure"))
                : empty.ReplayAsync(call.Arg<SavedMapId>(), call.Arg<SavedMapOperationCursor>()));
        return repository;
    }

    private static RealtimeDelivery.Event Operation(long sequence, long cursor) => new(new RealtimeEvent(
        RealtimeFamily.CollabOps, sequence, "map-a", "operation",
        JsonSerializer.SerializeToElement(new { serverCursor = cursor, operationId = "operation" }), DateTimeOffset.UtcNow));
}
