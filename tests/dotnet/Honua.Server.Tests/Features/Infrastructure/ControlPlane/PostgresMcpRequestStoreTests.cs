// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresMcpRequestStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Alice = "sub:alice";
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);

    // One poll interval, plus the poll's own query budget.
    private static readonly TimeSpan _onePoll = McpRequestBounds.PollInterval + TimeSpan.FromSeconds(2);

    [IntegrationTest]
    public async Task Register_ThirtyThreeConcurrentRequests_AdmitsThirtyTwo()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.CreateMcpSessionStore().SaveAsync(Record("busy-session"), _idleTimeout, CancellationToken.None);

        var states = await Task.WhenAll(Enumerable.Range(0, 33).Select(index =>
            harness.CreateMcpSessionStore().UpdateRequestAsync("busy-session", Alice, $"request-{index}",
                Guid.NewGuid().ToString("N"), McpRequestOperation.Register, CancellationToken.None)));

        states.Count(state => state == McpRequestState.Active).Should().Be(McpRequestBounds.PerSession);
        states.Count(state => state == McpRequestState.Capacity).Should().Be(1);
    }

    [IntegrationTest]
    public async Task Register_AcrossSessionsOfOnePrincipal_AdmitsOneHundredTwentyEight()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateMcpSessionStore();
        var admitted = 0;
        for (var session = 0; session < 5; session++)
        {
            await store.SaveAsync(Record($"session-{session}"), _idleTimeout, CancellationToken.None);
            for (var request = 0; request < 30; request++)
            {
                var state = await store.UpdateRequestAsync($"session-{session}", Alice, $"request-{request}",
                    Guid.NewGuid().ToString("N"), McpRequestOperation.Register, CancellationToken.None);
                admitted += state == McpRequestState.Active ? 1 : 0;
            }
        }

        admitted.Should().Be(McpRequestBounds.PerPrincipal);
    }

    [IntegrationTest]
    public async Task Register_SameRequestIdTwice_IsADuplicateUntilFinished()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateMcpSessionStore();
        await store.SaveAsync(Record("once-session"), _idleTimeout, CancellationToken.None);

        (await Update(store, "first", McpRequestOperation.Register)).Should().Be(McpRequestState.Active);
        (await Update(store, "second", McpRequestOperation.Register)).Should().Be(McpRequestState.Duplicate);
        (await Update(store, "second", McpRequestOperation.Finish)).Should().Be(McpRequestState.Missing);
        (await Update(store, "first", McpRequestOperation.Finish)).Should().Be(McpRequestState.Active);
        (await Update(store, "second", McpRequestOperation.Register)).Should().Be(McpRequestState.Active);

        static Task<McpRequestState> Update(IMcpRequestStore store, string generation, McpRequestOperation operation)
            => store.UpdateRequestAsync("once-session", Alice, "request", generation, operation, CancellationToken.None);
    }

    [IntegrationTest]
    public async Task Cancel_OnAnotherStore_IsObservedByTheOwnerWithinOnePoll()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = new McpSessionManager(idleTimeout: _idleTimeout, store: harness.CreateMcpSessionStore());
        var other = new McpSessionManager(idleTimeout: _idleTimeout, store: harness.CreateMcpSessionStore());
        var session = (await owner.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, state) = await owner.StartRequestAsync(session, Alice, RequestId("7"), CancellationToken.None);
        state.Should().Be(McpRequestState.Active);
        await using var request = lease!;
        var stopped = Task.Delay(Timeout.Infinite, request.Token);

        await other.CancelRequestAsync(session, Alice, RequestId("7"), CancellationToken.None);

        await FluentActions.Awaiting(() => stopped.WaitAsync(_onePoll)).Should().ThrowAsync<OperationCanceledException>();
        (await request.CompleteAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Remove_OnAnotherStore_EndsTheLocalSessionWithinOnePoll()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = new McpSessionManager(idleTimeout: _idleTimeout, store: harness.CreateMcpSessionStore());
        var other = new McpSessionManager(idleTimeout: _idleTimeout, store: harness.CreateMcpSessionStore());
        var session = (await owner.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        owner.TryGetReader(session, out var stream).Should().BeTrue();
        var lifetime = owner.GetLifetimeToken(session);

        await other.TerminateAsync(session, CancellationToken.None);

        await FluentActions.Awaiting(() => Task.Delay(Timeout.Infinite, lifetime).WaitAsync(_onePoll))
            .Should().ThrowAsync<OperationCanceledException>();
        stream.Completion.IsCompleted.Should().BeTrue("the local stream of a removed session ends");
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresMcpRequestStoreTests));

    private static McpSessionRecord Record(string sessionId)
        => new(sessionId, Alice, ElicitationSupported: false, WorkflowView: null, DateTimeOffset.UtcNow);

    private static JsonElement RequestId(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
