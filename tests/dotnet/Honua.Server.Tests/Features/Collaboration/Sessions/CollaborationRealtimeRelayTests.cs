// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed class CollaborationRealtimeRelayTests
{
    [UnitTest]
    public async Task Reset_ResumesFromSuppliedHead_WhenWindowAdvancesDuringRecovery()
    {
        var after = await ObserveReconnectAsync(reset: true);
        after.Should().Be(12, "operations after the reset boundary must remain deliverable");
    }

    [UnitTest]
    public async Task Exception_ResumesAfterLastSuccessfulDelivery()
    {
        var after = await ObserveReconnectAsync(reset: false);
        after.Should().Be(11, "a later delivery failure must not replay completed deliveries");
    }

    private static async Task<long?> ObserveReconnectAsync(bool reset)
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var repository = Substitute.For<ISavedMapOperationLogRepository>();
        var empty = new InMemorySavedMapOperationLogRepository(new MvpSavedMapOperationConflictPolicy());
        repository.ReplayAsync(Arg.Any<SavedMapId>(), Arg.Any<SavedMapOperationCursor>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<SavedMapOperationCursor>().Value == 1
                ? Task.FromException<SavedMapOperationReplayResult>(new IOException("Transient replay failure"))
                : empty.ReplayAsync(call.Arg<SavedMapId>(), call.Arg<SavedMapOperationCursor>()));
        var subscriptions = 0;
        var reconnect = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.GetWindowAsync(Arg.Any<RealtimeFamily>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(new RealtimeLogWindow(call.Arg<RealtimeFamily>(), 1, subscriptions == 0 ? 10 : 13)));
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(call =>
        {
            var request = call.Arg<RealtimeSubscriptionRequest>();
            if (request.Family == RealtimeFamily.CollabOps)
            {
                if (Interlocked.Increment(ref subscriptions) == 1)
                {
                    if (reset)
                    {
                        request.Writer.TryWrite(new RealtimeDelivery.Reset(RealtimeResetReasons.SlowConsumer, 12));
                    }
                    else
                    {
                        request.Writer.TryWrite(Operation(11, 1));
                        request.Writer.TryWrite(Operation(12, 2));
                    }
                }
                else
                {
                    reconnect.TrySetResult(request.After);
                }
            }
            return Substitute.For<IRealtimeSubscription>();
        });
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(new InMemoryCollaborationSessionService(
            Substitute.For<ISavedMapCollaborationAuthorizer>(), new SystemCollaborationSessionClock()));
        await using var container = services.BuildServiceProvider();
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

    private static RealtimeDelivery.Event Operation(long sequence, long cursor) => new(new RealtimeEvent(
        RealtimeFamily.CollabOps, sequence, "map-a", "operation",
        JsonSerializer.SerializeToElement(new { serverCursor = cursor, operationId = "operation" }), DateTimeOffset.UtcNow));
}
