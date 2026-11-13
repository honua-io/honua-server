// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresMcpSessionStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Alice = "sub:alice";
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);

    [IntegrationTest]
    public async Task TouchAsync_WithinTheLastMinute_DoesNotWrite()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateMcpSessionStore();
        await store.SaveAsync(Record("touch-session"), _idleTimeout, CancellationToken.None);
        var saved = await ExpiresAtAsync(harness, "touch-session");

        (await store.TouchAsync("touch-session", _idleTimeout, CancellationToken.None)).Should().BeTrue();
        (await ExpiresAtAsync(harness, "touch-session")).Should().Be(saved, "a session touched within the last minute is not rewritten");

        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("mcp_sessions")} SET expires_at = now() + interval '28 minutes' WHERE session_id = 'touch-session'");
        var aged = await ExpiresAtAsync(harness, "touch-session");
        (await store.TouchAsync("touch-session", _idleTimeout, CancellationToken.None)).Should().BeTrue();
        (await ExpiresAtAsync(harness, "touch-session")).Should().BeAfter(aged.AddMinutes(1));
    }

    [IntegrationTest]
    public async Task GetAsync_AfterIdleTimeout_ReturnsNull()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateMcpSessionStore();
        await store.SaveAsync(Record("idle-session"), _idleTimeout, CancellationToken.None);
        (await store.GetAsync("idle-session", CancellationToken.None))!.PrincipalKey.Should().Be(Alice);

        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("mcp_sessions")} SET expires_at = now() - interval '1 second' WHERE session_id = 'idle-session'");

        (await store.GetAsync("idle-session", CancellationToken.None)).Should().BeNull();
        (await store.TouchAsync("idle-session", _idleTimeout, CancellationToken.None)).Should().BeFalse();
        (await store.ExistsAsync("idle-session", CancellationToken.None)).Should().BeFalse();
        (await store.DeleteExpiredAsync(100, CancellationToken.None)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task SessionCreatedOnOneNode_IsAcceptedOnAnotherWithItsNegotiatedState()
    {
        await using var harness = await CreateHarnessAsync();
        var a = new McpSessionManager(idleTimeout: _idleTimeout, store: harness.CreateMcpSessionStore());
        var b = new McpSessionManager(idleTimeout: _idleTimeout, store: harness.CreateMcpSessionStore());

        var id = await a.TryCreateSessionAsync(Alice, elicitationSupported: true, "setup", CancellationToken.None);

        (await b.ValidateAccessAsync(id!, Alice, CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        b.GetWorkflowView(id!).Should().Be("setup");
        b.SupportsElicitation(id!).Should().BeTrue();
        (await b.ValidateAccessAsync(id!, "sub:mallory", CancellationToken.None)).Should().Be(McpSessionValidation.PrincipalMismatch);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresMcpSessionStoreTests));

    private static McpSessionRecord Record(string sessionId)
        => new(sessionId, Alice, ElicitationSupported: false, WorkflowView: null, DateTimeOffset.UtcNow);

    private static async Task<DateTime> ExpiresAtAsync(ControlPlaneStateHarness harness, string sessionId)
        => await harness.ScalarAsync<DateTime>(
            $"SELECT expires_at FROM {harness.Table("mcp_sessions")} WHERE session_id = '{sessionId}'");
}
