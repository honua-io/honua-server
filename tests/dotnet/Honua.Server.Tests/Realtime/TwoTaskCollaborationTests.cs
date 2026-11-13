// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Collaboration;
using Honua.Server.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class TwoTaskCollaborationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ConcurrentConflictingEdits_BothTasksSeeOnlyTheCommittedWinner()
    {
        await using var harness = await CreateHarnessAsync();
        await using var first = await TaskNode.StartAsync(harness);
        await using var second = await TaskNode.StartAsync(harness);
        var alice = await first.JoinAsync("alice");
        var bob = await second.JoinAsync("bob");
        var results = await Task.WhenAll(
            first.Coordinator.AppendAndPublishAsync("map-a", Request("alice"), CancellationToken.None),
            second.Coordinator.AppendAndPublishAsync("map-a", Request("bob"), CancellationToken.None));
        results.Count(r => r.Status == SavedMapOperationAppendStatus.Accepted).Should().Be(1);
        results.Count(r => r.Status == SavedMapOperationAppendStatus.Conflict).Should().Be(1);
        var winner = results.Single(r => r.Status == SavedMapOperationAppendStatus.Accepted).Operation!;
        var a = await ReadOperationAsync(first.Sessions, alice.SessionId);
        var b = await ReadOperationAsync(second.Sessions, bob.SessionId);
        a.Id.Should().Be(winner.OperationId.Value);
        b.Id.Should().Be(winner.OperationId.Value);
        a.Cursor.Should().Be("1");
        b.Cursor.Should().Be("1");
        (await harness.ReadSequencesAsync("collab-ops")).Should().Equal(1L);
    }

    [IntegrationTest]
    public async Task Presence_IsDeliveredWithin750Milliseconds_AndRebuiltByReplacementTask()
    {
        await using var harness = await CreateHarnessAsync();
        await using var first = await TaskNode.StartAsync(harness);
        await using var second = await TaskNode.StartAsync(harness);
        var alice = await first.JoinAsync("alice");
        var bob = await second.JoinAsync("bob");
        var watch = Stopwatch.StartNew();
        first.Sessions.UpdateCursor(alice.SessionId, new CollaborationCursor { X = 12, Y = 34 });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            while (!second.Sessions.DrainEvents(bob.SessionId).Any(e =>
                e.Event.Type == CollaborationSessionEventTypes.Cursor && e.Event.Cursor?.X == 12))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(750));
        await second.DisposeAsync();
        await using var replacement = await TaskNode.StartAsync(harness);
        var cara = await replacement.JoinAsync("cara");
        cara.Snapshot.Participants.Should().Contain(p => p.Id == alice.ParticipantId);
        cara.Snapshot.Cursors[alice.ParticipantId].X.Should().Be(12);
        replacement.Sessions.Leave(alice.SessionId).Should().BeFalse();

        var accepted = await first.Coordinator.AppendAndPublishAsync("map-a", Request("first"), CancellationToken.None);
        accepted.Status.Should().Be(SavedMapOperationAppendStatus.Accepted);
        await using var next = await TaskNode.StartAsync(harness);
        var resumed = await next.Repository.ReplayAsync(new SavedMapId("map-a"), accepted.HeadCursor);
        resumed.Operations.Should().BeEmpty();
        var request = Request("next") with { BaseCursor = accepted.HeadCursor };
        var appended = await first.Coordinator.AppendAndPublishAsync("map-a", request, CancellationToken.None);
        var replay = await next.Repository.ReplayAsync(new SavedMapId("map-a"), accepted.HeadCursor);
        replay.Operations.Should().ContainSingle(op => op.OperationId == appended.Operation!.OperationId);
    }

    private async Task<RealtimeLogHarness> CreateHarnessAsync()
    {
        var harness = await RealtimeLogHarness.CreateAsync(database);
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.092_CreateSavedMapOperationLog.sql");
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.161_PublishSavedMapOperationsToRealtime.sql");
        return harness;
    }

    private static SavedMapOperationAppendRequest Request(string actor) => new()
    {
        MapId = new SavedMapId("map-a"),
        ActorId = new SavedMapActorId(actor),
        OperationId = new SavedMapOperationId(Guid.NewGuid().ToString("N")),
        BaseCursor = SavedMapOperationCursor.Empty,
        Kind = SavedMapOperationKind.ReplaceWebMapDocument,
        Payload = RealtimeLogHarness.Payload(new { title = actor })
    };

    private static async Task<CollaborationOperationWire> ReadOperationAsync(InMemoryCollaborationSessionService sessions, Guid session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var operations = sessions.DrainEvents(session).Where(e => e.Event.Type == CollaborationSessionEventTypes.OperationAppended).ToArray();
            if (operations.Length > 0)
            {
                operations.Should().ContainSingle();
                return operations[0].Event.Operation!;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TaskNode : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly RealtimeTailBackgroundService _tail;
        private readonly CollaborationRealtimeRelay _relay;
        private bool _disposed;
        public InMemoryCollaborationSessionService Sessions { get; }
        public ISavedMapOperationLogRepository Repository { get; }
        public SavedMapOperationAppendCoordinator Coordinator { get; }

        private TaskNode(RealtimeLogHarness harness)
        {
            var options = new RealtimeOptions { LocalPollInterval = TimeSpan.FromMilliseconds(20), WakeCoalesceWindow = TimeSpan.Zero };
            var dispatcher = harness.CreateDispatcher(options);
            var presence = new RealtimePresenceBatcher(harness.Log);
            IRealtimeFanoutProvider provider = new PostgresRealtimeFanoutProvider(harness.Log, dispatcher, presence);
            Repository = PostgresCollaborationTestStore.Create(harness.DataSource, harness.Schema);
            var registrations = new ServiceCollection();
            registrations.AddSingleton(Repository);
            registrations.AddSingleton(provider);
            registrations.AddSingleton<ICollaborationSessionClock, SystemCollaborationSessionClock>();
            registrations.AddSingleton<ISavedMapCollaborationAuthorizer, AllowAuthorizer>();
            registrations.AddSingleton(sp => new CollaborationRealtimeRelay(provider, sp, NullLogger<CollaborationRealtimeRelay>.Instance));
            registrations.AddSingleton<InMemoryCollaborationSessionService>();
            _services = registrations.BuildServiceProvider();
            Sessions = _services.GetRequiredService<InMemoryCollaborationSessionService>();
            _relay = _services.GetRequiredService<CollaborationRealtimeRelay>();
            Coordinator = new SavedMapOperationAppendCoordinator(Repository, Sessions, SavedMapCollaborationTopology.ForMultiReplica(true), provider);
            _tail = new RealtimeTailBackgroundService(dispatcher, presence, null, Options.Create(options), NullLogger<RealtimeTailBackgroundService>.Instance);
        }

        public static async Task<TaskNode> StartAsync(RealtimeLogHarness harness)
        {
            var node = new TaskNode(harness);
            await node._tail.StartAsync(CancellationToken.None);
            await node._relay.StartAsync(CancellationToken.None);
            await node._relay.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            return node;
        }

        public async Task<CollaborationJoinResponse> JoinAsync(string user)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "Test"));
            return (await Sessions.JoinAsync("map-a", new CollaborationJoinRequest { DisplayName = user }, principal)).Response!;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _relay.StopAsync(CancellationToken.None);
            await _tail.StopAsync(CancellationToken.None);
            Coordinator.Dispose();
            _tail.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class AllowAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(string mapId, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Allow());
    }
}
