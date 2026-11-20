// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
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
    public async Task PublishMessage_SessionOnAnotherTask_PersistsJsonRpcNotification()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        await using var remoteSessions = new McpSessionManager();
        await using var localSessions = new McpSessionManager();
        var sessionId = remoteSessions.CreateSession();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(localSessions)
            .AddSingleton<IRealtimeFanoutProvider>(new PostgresRealtimeFanoutProvider(
                harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log)))
            .BuildServiceProvider();
        var publisher = ActivatorUtilities.CreateInstance<McpNotificationPublisher>(services);
        (await publisher.PublishMessage(sessionId, "info", "honua.jobs", Status("job-remote", 42))).Should().BeTrue();
        var events = await harness.Log.ReadAfterAsync(new RealtimeLogRead("mcp", 0, 10), CancellationToken.None);
        var persisted = events.Should().ContainSingle().Subject;
        persisted.ScopeKey.Should().Be(sessionId);
        persisted.Payload.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        persisted.Payload.GetProperty("method").GetString().Should().Be("notifications/message");
        var data = persisted.Payload.GetProperty("params").GetProperty("data");
        data.GetProperty("jobId").GetString().Should().Be("job-remote");
        data.GetProperty("progress").GetDouble().Should().Be(42);
    }

    [IntegrationTest]
    public async Task StreamOnAnotherTask_ReplaysOnlyItsSessionAndBroadcast_ThenResumesWithoutDuplicates()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var publisherProvider = new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        var consumerProvider = new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        await using var publisherSessions = new McpSessionManager();
        var publisher = new McpNotificationPublisher(publisherSessions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<McpNotificationPublisher>.Instance, publisherProvider);
        await publisher.PublishMessage("session-a", "info", "honua.jobs", Status("job-a", 25));
        await publisher.PublishMessage("session-b", "info", "honua.jobs", Status("job-b", 50));
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

        await publisher.PublishMessage("session-a", "info", "honua.jobs", Status("job-a", 100));
        await using var resumed = new RealtimeStreamSubscription(consumerProvider, "session-a", resume, lease);
        var next = (RealtimeDelivery.Event)await resumed.Reader.ReadAsync(timeout.Token);
        next.Value.Sequence.Should().BeGreaterThan(resume);
        next.Value.Payload.GetProperty("params").GetProperty("data").GetProperty("progress").GetDouble().Should().Be(100);
    }

    private static JsonElement Status(string jobId, double progress)
    {
        using var document = JsonDocument.Parse($$"""{"jobId":"{{jobId}}","progress":{{progress}}}""");
        return document.RootElement.Clone();
    }
}
