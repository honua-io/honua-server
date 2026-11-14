// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.Mcp)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeMcpNotificationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task PublishProgress_SessionOnAnotherTask_PersistsJsonRpcNotification()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var remoteSessions = new McpSessionManager();
        var sessionId = remoteSessions.CreateSession();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new McpSessionManager())
            .AddSingleton<IRealtimeFanoutProvider>(new PostgresRealtimeFanoutProvider(
                harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log)))
            .BuildServiceProvider();
        var publisher = ActivatorUtilities.CreateInstance<McpNotificationPublisher>(services);
        (await publisher.PublishProgress(sessionId, "job-remote", 42, 100, "Running")).Should().BeTrue();
        var events = await harness.Log.ReadAfterAsync(new RealtimeLogRead("mcp", 0, 10), CancellationToken.None);
        var persisted = events.Should().ContainSingle().Subject;
        persisted.ScopeKey.Should().Be(sessionId);
        persisted.Payload.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        persisted.Payload.GetProperty("method").GetString().Should().Be("notifications/progress");
        var parameters = persisted.Payload.GetProperty("params");
        parameters.GetProperty("progressToken").GetString().Should().Be("job-remote");
        parameters.GetProperty("progress").GetDouble().Should().Be(42);
    }

    [IntegrationTest]
    public async Task StreamOnAnotherTask_ReplaysOnlyItsSessionAndBroadcast_ThenResumesWithoutDuplicates()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var publisherProvider = new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        var consumerProvider = new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        var publisher = new McpNotificationPublisher(new McpSessionManager(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<McpNotificationPublisher>.Instance, publisherProvider);
        await publisher.PublishProgress("session-a", "job-a", 25, 100, "Running");
        await publisher.PublishProgress("session-b", "job-b", 50, 100, "Running");
        await publisher.BroadcastToolsListChanged();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lease = Substitute.For<IRealtimeStreamLease>();
        lease.IsHeld.Returns(true);
        long resume;
        await using (var subscription = new RealtimeStreamSubscription(consumerProvider, "session-a", 0, lease))
        {
            var first = (RealtimeDelivery.Event)await subscription.Reader.ReadAsync(timeout.Token);
            var second = (RealtimeDelivery.Event)await subscription.Reader.ReadAsync(timeout.Token);
            first.Value.ScopeKey.Should().Be("session-a");
            second.Value.ScopeKey.Should().Be("*");
            second.Value.Sequence.Should().BeGreaterThan(first.Value.Sequence);
            resume = second.Value.Sequence;
        }

        await publisher.PublishProgress("session-a", "job-a", 100, 100, "Complete");
        await using var resumed = new RealtimeStreamSubscription(consumerProvider, "session-a", resume, lease);
        var next = (RealtimeDelivery.Event)await resumed.Reader.ReadAsync(timeout.Token);
        next.Value.Sequence.Should().BeGreaterThan(resume);
        next.Value.Payload.GetProperty("params").GetProperty("progress").GetDouble().Should().Be(100);
    }
}
