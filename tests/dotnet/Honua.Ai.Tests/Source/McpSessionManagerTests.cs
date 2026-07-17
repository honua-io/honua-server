// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit tests for the MCP Streamable-HTTP session registry (honua-server#1954).
/// These run without Docker/PostGIS so the session-id issuance and validation
/// invariants are covered even when the full integration harness is unavailable.
/// </summary>
public sealed class McpSessionManagerTests
{
    [UnitTest]
    public void CreateSession_IssuesUniqueVisibleAsciiIds()
    {
        var manager = new McpSessionManager();

        var ids = Enumerable.Range(0, 100).Select(_ => manager.CreateSession()).ToArray();

        ids.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));
        ids.Distinct().Should().HaveCount(ids.Length, "session ids must be globally unique");
        // The spec requires session ids to contain only visible ASCII (0x21-0x7E).
        ids.Should().OnlyContain(id => id.All(c => c >= '!' && c <= '~'));
    }

    [UnitTest]
    public void IsValid_ReturnsTrueForIssuedSession_AndFalseOtherwise()
    {
        var manager = new McpSessionManager();
        var id = manager.CreateSession();

        manager.IsValid(id).Should().BeTrue();
        manager.IsValid("never-issued").Should().BeFalse();
        manager.IsValid(string.Empty).Should().BeFalse();
        manager.IsValid(null!).Should().BeFalse();
    }

    [UnitTest]
    public void SupportsElicitation_ReflectsCapabilityRecordedAtCreation()
    {
        // honua-server#2484: the elicitation capability advertised at initialize is
        // bound to the session and read back by clarification-emitting tools.
        var manager = new McpSessionManager();
        manager.TryCreateSession("sub:alice", elicitationSupported: true, out var elicit)
            .Should().BeTrue();
        manager.TryCreateSession("sub:bob", elicitationSupported: false, out var plain)
            .Should().BeTrue();

        manager.SupportsElicitation(elicit).Should().BeTrue();
        manager.SupportsElicitation(plain).Should().BeFalse();
        // The default two-arg overload never advertises elicitation.
        manager.SupportsElicitation(manager.CreateSession()).Should().BeFalse();
        manager.SupportsElicitation("never-issued").Should().BeFalse();
        manager.SupportsElicitation(string.Empty).Should().BeFalse();
    }

    [UnitTest]
    public void SupportsElicitation_ReturnsFalseAfterTermination()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession("sub:alice", elicitationSupported: true, out var id)
            .Should().BeTrue();
        manager.SupportsElicitation(id).Should().BeTrue();

        manager.Terminate(id).Should().BeTrue();
        manager.SupportsElicitation(id).Should().BeFalse();
    }

    [UnitTest]
    public void Terminate_RemovesSession_AndIsIdempotent()
    {
        var manager = new McpSessionManager();
        var id = manager.CreateSession();

        manager.Terminate(id).Should().BeTrue();
        manager.IsValid(id).Should().BeFalse();
        // A second termination of the same id reports no active session removed.
        manager.Terminate(id).Should().BeFalse();
        manager.Terminate("never-issued").Should().BeFalse();
    }

    [UnitTest]
    public void ValidateAccess_BindsToPrincipal_AndRejectsDifferentPrincipal()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession("JwtBearer:sub:alice", out var id).Should().BeTrue();

        manager.ValidateAccess(id, "JwtBearer:sub:alice").Should().Be(McpSessionValidation.Valid);
        // Anonymous caller cannot ride a session bound to an authenticated principal.
        manager.ValidateAccess(id, McpSessionManager.AnonymousPrincipalKey)
            .Should().Be(McpSessionValidation.PrincipalMismatch);
        // A different authenticated identity is likewise rejected.
        manager.ValidateAccess(id, "JwtBearer:sub:bob").Should().Be(McpSessionValidation.PrincipalMismatch);
        // Same subject under a different auth scheme is also rejected.
        manager.ValidateAccess(id, "ApiKey:name:alice").Should().Be(McpSessionValidation.PrincipalMismatch);
        // An unknown id is unknown regardless of principal.
        manager.ValidateAccess("never-issued", "JwtBearer:sub:alice").Should().Be(McpSessionValidation.Unknown);
    }

    [UnitTest]
    public void ValidateAccess_AnonymousSession_AcceptsAnonymousCaller()
    {
        var manager = new McpSessionManager();
        var id = manager.CreateSession(); // anonymous binding

        manager.ValidateAccess(id, McpSessionManager.AnonymousPrincipalKey)
            .Should().Be(McpSessionValidation.Valid);
        manager.ValidateAccess(id, "sub:alice").Should().Be(McpSessionValidation.PrincipalMismatch);
    }

    [UnitTest]
    public void ValidateAccess_AfterIdleTimeout_ExpiresSession()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var manager = new McpSessionManager(
            maxSessions: 100,
            idleTimeout: TimeSpan.FromMinutes(30),
            timeProvider: time);
        manager.TryCreateSession("sub:alice", out var id).Should().BeTrue();

        // Within the window the session is valid and the access slides the window.
        time.Advance(TimeSpan.FromMinutes(29));
        manager.ValidateAccess(id, "sub:alice").Should().Be(McpSessionValidation.Valid);

        // 29 more minutes from the refreshed access is still under 30.
        time.Advance(TimeSpan.FromMinutes(29));
        manager.ValidateAccess(id, "sub:alice").Should().Be(McpSessionValidation.Valid);

        // Past the idle timeout with no access, the session expires (→ 404).
        time.Advance(TimeSpan.FromMinutes(31));
        manager.ValidateAccess(id, "sub:alice").Should().Be(McpSessionValidation.Unknown);
        manager.IsValid(id).Should().BeFalse("an expired session is swept");
    }

    [UnitTest]
    public void TryCreateSession_AtCapacityWithRejectPolicy_RefusesNewSession()
    {
        var manager = new McpSessionManager(
            maxSessions: 2,
            evictionPolicy: McpSessionEvictionPolicy.RejectNew);

        manager.TryCreateSession("sub:a", out var first).Should().BeTrue();
        manager.TryCreateSession("sub:b", out _).Should().BeTrue();

        manager.TryCreateSession("sub:c", out var rejected).Should().BeFalse();
        rejected.Should().BeEmpty();
        // Existing sessions are untouched under the reject policy.
        manager.IsValid(first).Should().BeTrue();
    }

    [UnitTest]
    public void TryCreateSession_AtCapacityWithLruPolicy_EvictsLeastRecentlyUsed()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var manager = new McpSessionManager(
            maxSessions: 2,
            idleTimeout: TimeSpan.FromHours(1),
            evictionPolicy: McpSessionEvictionPolicy.EvictLeastRecentlyUsed,
            timeProvider: time);

        manager.TryCreateSession("sub:a", out var oldest).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(1));
        manager.TryCreateSession("sub:b", out var middle).Should().BeTrue();

        // Touch the oldest so it is no longer the LRU victim.
        time.Advance(TimeSpan.FromMinutes(1));
        manager.ValidateAccess(oldest, "sub:a").Should().Be(McpSessionValidation.Valid);

        // Admitting a third session evicts the true LRU (now `middle`).
        time.Advance(TimeSpan.FromMinutes(1));
        manager.TryCreateSession("sub:c", out var newest).Should().BeTrue();

        manager.IsValid(middle).Should().BeFalse("the least-recently-used session is evicted");
        manager.IsValid(oldest).Should().BeTrue();
        manager.IsValid(newest).Should().BeTrue();
    }

    /// <summary>
    /// Test <see cref="TimeProvider"/> whose UTC clock only advances when the test
    /// asks, so idle-TTL and LRU behavior are deterministic without real waits.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
