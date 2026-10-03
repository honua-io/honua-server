// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using FluentAssertions;
using Xunit;

namespace Honua.Server.Tests.Features.HealthCheck;

/// <summary>
/// Replay rules for the audit hash chain: an unhashed row after the chain has started
/// does not verify, and a hashed chain does not verify without a configured key.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class AuditChainLinkTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x21, 32).ToArray();
    private static readonly DateTimeOffset When = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingHash_AfterAHashedRow_DoesNotVerify()
    {
        var first = Link(1, "auth.failure", previous: null, keyed: false);
        var gap = Link(2, "auth.token.issue", previous: null, keyed: false) with { EntryHash = null };

        var report = Walk(ReadOnlyMemory<byte>.Empty, first, gap);

        report.Verified.Should().BeFalse();
        report.FailureReason.Should().Contain("unhashed");
        report.FirstBrokenAuditId.Should().Be(2);
        report.RowsChecked.Should().Be(2);
        report.UnhashedRows.Should().Be(0);
    }

    [Fact]
    public void LeadingUnhashedRow_ThenKeyedRow_Verifies()
    {
        var leading = Link(1, "auth.failure", previous: null, keyed: false) with { EntryHash = null, PreviousHash = null };
        var keyed = Link(2, "auth.token.issue", previous: null, keyed: true);

        var report = Walk(Key, leading, keyed);

        report.Verified.Should().BeTrue();
        report.UnhashedRows.Should().Be(1);
        report.RowsChecked.Should().Be(2);
    }

    [Fact]
    public void HashedRows_WithoutChainKey_DoNotVerify()
    {
        var failure = Link(1, "auth.failure", previous: null, keyed: false);
        var issued = Link(2, "auth.token.issue", previous: failure.EntryHash, keyed: false);

        var report = Walk(ReadOnlyMemory<byte>.Empty, failure, issued);

        report.Verified.Should().BeFalse();
        report.FailureReason.Should().Contain("key");
        report.RowsChecked.Should().Be(2);
    }

    [Fact]
    public void LegacyRows_WithKeyConfigured_DoNotVerifyUntilAKeyedRowExists()
    {
        var legacy = Link(1, "auth.failure", previous: null, keyed: false);

        var report = Walk(Key, legacy);

        report.Verified.Should().BeFalse();
        report.FailureReason.Should().Contain("keyed");
    }

    [Fact]
    public void AuthenticationOutcomes_AreDistinctKeyedLinks()
    {
        var failure = Link(1, "auth.failure", previous: null, keyed: true);
        var issued = Link(2, "auth.token.issue", previous: failure.EntryHash, keyed: true);

        failure.EntryHash.Should().NotBe(issued.EntryHash);
        failure.EntryHash.Should().NotBe(LegacyHash(failure));
        issued.EntryHash.Should().Be(MacHash(issued));

        var report = Walk(Key, failure, issued);
        report.Verified.Should().BeTrue();
        report.RowsChecked.Should().Be(2);
        report.UnhashedRows.Should().Be(0);
    }

    [Fact]
    public void LegacyPrefix_FollowedByKeyedRow_Verifies()
    {
        var legacy = Link(1, "auth.failure", previous: null, keyed: false);
        var keyed = Link(2, "auth.token.issue", previous: legacy.EntryHash, keyed: true);

        Walk(Key, legacy, keyed).Verified.Should().BeTrue();
    }

    [Fact]
    public void WrongKey_DoesNotVerify()
    {
        var row = Link(1, "auth.failure", previous: null, keyed: true);
        var other = Enumerable.Repeat((byte)0x22, 32).ToArray();

        Walk(other, row).Verified.Should().BeFalse();
    }

    [Fact]
    public void AbsentKey_DecodesEmpty_AndShortKey_FailsClosed()
    {
        AuditChainKeyMaterial.Decode(null).Length.Should().Be(0);
        AuditChainKeyMaterial.Decode("  ").Length.Should().Be(0);

        var act = () => AuditChainKeyMaterial.Decode(Convert.ToBase64String(new byte[8]));
        act.Should().Throw<InvalidOperationException>();
    }

    private static AuditIntegrityReport Walk(ReadOnlyMemory<byte> chainKey, params AuditChainLink[] rows)
    {
        var cursor = new AuditChainVerificationCursor();
        foreach (var row in rows)
        {
            var failed = cursor.Observe(row, chainKey);
            if (failed is not null)
            {
                return failed;
            }
        }

        return cursor.Complete(chainKey);
    }

    private static AuditChainLink Link(long id, string action, string? previous, bool keyed)
    {
        var bare = new AuditChainLink
        {
            AuditId = id,
            Timestamp = When,
            EventType = AuditEventType.Authentication,
            Actor = "user-1",
            ActorType = AuditActorType.UserId,
            ResourceType = "session",
            ResourceId = "/sharing/rest/generateToken",
            Action = action,
            Outcome = action == "auth.token.issue" ? AuditOutcome.Success : AuditOutcome.Failure,
            CorrelationId = "corr-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RemoteIp = "10.0.0.1",
            UserAgent = "agent/1.0",
            Details = "{}",
            PreviousHash = previous,
        };

        return bare with { EntryHash = keyed ? MacHash(bare) : LegacyHash(bare) };
    }

    private static string LegacyHash(in AuditChainLink link)
        => AuditEntryHasher.ComputeEntryHash(
            link.PreviousHash,
            link.Timestamp,
            link.EventType,
            link.Actor,
            link.ActorType,
            link.ResourceType,
            link.ResourceId,
            link.Action,
            link.Outcome,
            link.CorrelationId,
            link.RemoteIp,
            link.UserAgent,
            link.Details);

    private static string MacHash(in AuditChainLink link)
        => AuditEntryHasher.ComputeEntryMac(
            Key,
            link.PreviousHash,
            link.Timestamp,
            link.EventType,
            link.Actor,
            link.ActorType,
            link.ResourceType,
            link.ResourceId,
            link.Action,
            link.Outcome,
            link.CorrelationId,
            link.RemoteIp,
            link.UserAgent,
            link.Details);
}
