// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using FluentAssertions;
using Xunit;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.AuditLog;
using Honua.Server.Features.Infrastructure.AuditLog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

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
    public void LegacyWrite_AfterKeyActivation_DoesNotVerify()
    {
        var legacy = Link(1, "auth.failure", previous: null, keyed: false);
        var keyed = Link(2, "auth.token.issue", previous: legacy.EntryHash, keyed: true);
        var oldReplica = Link(3, "auth.failure", previous: keyed.EntryHash, keyed: false);

        var report = Walk(Key, legacy, keyed, oldReplica);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(3);
        report.FailureReason.Should().Contain("entry_hash mismatch");
    }

    [Fact]
    public void WrongKey_DoesNotVerify()
    {
        var row = Link(1, "auth.failure", previous: null, keyed: true);
        var other = Enumerable.Repeat((byte)0x22, 32).ToArray();

        Walk(other, row).Verified.Should().BeFalse();
    }

    // Downgrade history the gap attestation exists for: a hashing build wrote rows 1-2,
    // a pre-hash-chain build appended rows 3-4 with no hash, the hashing build came back
    // and started a new chain at row 5 (its tail read found no hash), and the key was
    // activated before row 6.
    private static (AuditChainLink[] History, AuditChainGapAttestation Gap) DowngradeHistory()
    {
        var first = Link(1, "auth.failure", previous: null, keyed: false);
        var anchor = Link(2, "auth.token.issue", previous: first.EntryHash, keyed: false);
        var gapStart = Link(3, "auth.failure", previous: null, keyed: false) with { EntryHash = null };
        var gapEnd = Link(4, "auth.failure", previous: null, keyed: false) with { EntryHash = null };
        var resume = Link(5, "auth.failure", previous: null, keyed: false);
        var keyed = Link(6, "auth.token.issue", previous: resume.EntryHash, keyed: true);
        var gap = new AuditChainGapAttestation
        {
            AttestationAuditId = 7,
            AnchorAuditId = 2,
            AnchorEntryHash = anchor.EntryHash!,
            GapFirstAuditId = 3,
            GapLastAuditId = 4,
            GapRowCount = 2,
            ResumeAuditId = 5,
            ResumePreviousHash = null,
            Reason = "rolled back to a pre-hash-chain build",
        };

        return ([first, anchor, gapStart, gapEnd, resume, keyed], gap);
    }

    [Fact]
    public void DowngradeGap_WithoutAttestation_DoesNotVerify()
    {
        var (history, _) = DowngradeHistory();

        var report = Walk(Key, history);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(3);
        report.FailureReason.Should().Contain("unhashed row follows a hashed row");
    }

    [Fact]
    public void DowngradeGap_WithKeyedAttestation_VerifiesAndReportsTheGap()
    {
        var (history, gap) = DowngradeHistory();
        var attestation = AttestationRow(gap, previous: history[^1].EntryHash, keyed: true);

        var report = Walk(Key, [gap], [.. history, attestation]);

        report.Verified.Should().BeTrue(report.FailureReason);
        report.AttestedGapRows.Should().Be(2);
        report.UnhashedRows.Should().Be(0);
        report.RowsChecked.Should().Be(7);
    }

    [Fact]
    public void DowngradeGap_ResumedByLinkingOverTheGap_VerifiesWhenAttestedThatWay()
    {
        var (history, gap) = DowngradeHistory();
        var anchorHash = history[1].EntryHash;
        var resume = Link(5, "auth.failure", previous: anchorHash, keyed: false);
        var keyed = Link(6, "auth.token.issue", previous: resume.EntryHash, keyed: true);
        gap = gap with { ResumePreviousHash = anchorHash };
        var attestation = AttestationRow(gap, previous: keyed.EntryHash, keyed: true);

        var report = Walk(Key, [gap], [history[0], history[1], history[2], history[3], resume, keyed, attestation]);

        report.Verified.Should().BeTrue(report.FailureReason);
        report.AttestedGapRows.Should().Be(2);
    }

    [Fact]
    public void GapAttestation_WrittenAsLegacyDigest_DoesNotExcuseTheGap()
    {
        var (history, gap) = DowngradeHistory();
        var resume = history[4];
        var legacyAttestation = AttestationRow(gap with { AttestationAuditId = 6 }, previous: resume.EntryHash, keyed: false);
        var keyed = Link(7, "auth.token.issue", previous: legacyAttestation.EntryHash, keyed: true);

        var report = Walk(
            Key,
            [gap with { AttestationAuditId = 6 }],
            [.. history[..5], legacyAttestation, keyed]);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(6);
        report.FailureReason.Should().Contain("gap attestation");
    }

    [Fact]
    public void GapAttestation_NamingAnotherAnchorHash_DoesNotExcuseTheGap()
    {
        var (history, gap) = DowngradeHistory();
        var forged = gap with { AnchorEntryHash = new string('a', 64) };
        var attestation = AttestationRow(forged, previous: history[^1].EntryHash, keyed: true);

        var report = Walk(Key, [forged], [.. history, attestation]);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(3);
        report.FailureReason.Should().Contain("unhashed row follows a hashed row");
    }

    [Fact]
    public void GapAttestation_UnderstatingTheGap_DoesNotVerify()
    {
        var (history, gap) = DowngradeHistory();
        var narrow = gap with { GapLastAuditId = 3, GapRowCount = 1 };
        var attestation = AttestationRow(narrow, previous: history[^1].EntryHash, keyed: true);

        var report = Walk(Key, [narrow], [.. history, attestation]);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(4);
    }

    [Fact]
    public void GapAttestation_OverstatingTheGap_DoesNotVerify()
    {
        var (history, gap) = DowngradeHistory();
        var wide = gap with { GapLastAuditId = 5, GapRowCount = 3, ResumeAuditId = 6 };
        var attestation = AttestationRow(wide, previous: history[^1].EntryHash, keyed: true);

        var report = Walk(Key, [wide], [.. history, attestation]);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(5);
        report.FailureReason.Should().Contain("inside the gap");
    }

    [Fact]
    public void ResumeRow_WithAnotherPreviousHash_DoesNotVerify()
    {
        var (history, gap) = DowngradeHistory();
        var relinked = gap with { ResumePreviousHash = history[1].EntryHash };
        var attestation = AttestationRow(relinked, previous: history[^1].EntryHash, keyed: true);

        var report = Walk(Key, [relinked], [.. history, attestation]);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(5);
        report.FailureReason.Should().Contain("does not resume the chain");
    }

    [Fact]
    public void SecondUnattestedGap_AfterAnAttestedOne_DoesNotVerify()
    {
        var (history, gap) = DowngradeHistory();
        var attestation = AttestationRow(gap, previous: history[^1].EntryHash, keyed: true);
        var later = Link(8, "auth.failure", previous: null, keyed: false) with { EntryHash = null };

        var report = Walk(Key, [gap], [.. history, attestation, later]);

        report.Verified.Should().BeFalse();
        report.FirstBrokenAuditId.Should().Be(8);
        report.AttestedGapRows.Should().Be(2);
    }

    [Fact]
    public void GapAttestation_Details_RoundTrip_AndRejectInconsistentDeclarations()
    {
        var (_, gap) = DowngradeHistory();

        AuditChainGapAttestation.TryParse(7, gap.ToDetails()).Should().Be(gap);
        AuditChainGapAttestation.TryParse(7, gap.ToDetails().Replace("\"gapRowCount\":2", "\"gapRowCount\":3", StringComparison.Ordinal))
            .Should().BeNull("three rows cannot fit in ids 3-4");
        AuditChainGapAttestation.TryParse(5, gap.ToDetails()).Should().BeNull("the attestation must follow the resume row");
        AuditChainGapAttestation.TryParse(7, (gap with { ResumePreviousHash = new string('b', 64) }).ToDetails())
            .Should().BeNull("a resumed chain links to the anchor or starts fresh");
        AuditChainGapAttestation.TryParse(7, "{}").Should().BeNull();
        AuditChainGapAttestation.TryParse(7, "not json").Should().BeNull();
    }

    [Fact]
    public void AbsentKey_DecodesEmpty_AndShortKey_FailsClosed()
    {
        AuditChainKeyMaterial.Decode(null).Length.Should().Be(0);
        AuditChainKeyMaterial.Decode("  ").Length.Should().Be(0);

        var act = () => AuditChainKeyMaterial.Decode(Convert.ToBase64String(new byte[8]));
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("invalid-base64")]
    [InlineData(null)]
    [InlineData("IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=")]
    public void ConfigurationReload_KeepsStartupKeyAcrossScopes(string? reloadedKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AuditLog:ChainVerification:Key"] = Convert.ToBase64String(Key),
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Substitute.For<IAdoNetDatabaseConnectionProvider>());
        services.AddHonuaAuditLog();
        services.AddAuditChainVerification(configuration);
        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope();
        var snapshot = first.ServiceProvider.GetRequiredService<AuditChainKeySnapshot>();
        first.ServiceProvider.GetRequiredService<IAuditLog>();

        configuration["AuditLog:ChainVerification:Key"] = reloadedKey;
        configuration.Reload();

        using var second = provider.CreateScope();
        second.ServiceProvider.GetRequiredService<IAuditLog>();
        second.ServiceProvider.GetRequiredService<AuditChainKeySnapshot>().Should().BeSameAs(snapshot);
        snapshot.Key.ToArray().Should().Equal(Key);
    }

    private static AuditIntegrityReport Walk(ReadOnlyMemory<byte> chainKey, params AuditChainLink[] rows)
        => Walk(chainKey, [], rows);

    private static AuditIntegrityReport Walk(
        ReadOnlyMemory<byte> chainKey,
        IReadOnlyList<AuditChainGapAttestation> attestations,
        AuditChainLink[] rows)
    {
        var cursor = new AuditChainVerificationCursor(attestations);
        foreach (var failed in rows.Select(row => cursor.Observe(row, chainKey)))
        {
            if (failed is not null)
            {
                return failed;
            }
        }

        return cursor.Complete(chainKey);
    }

    private static AuditChainLink AttestationRow(AuditChainGapAttestation gap, string? previous, bool keyed)
    {
        var bare = Link(gap.AttestationAuditId, AuditChainGapAttestation.Action, previous, keyed: false) with
        {
            EventType = AuditEventType.AdminAction,
            ResourceType = AuditChainGapAttestation.ResourceType,
            ResourceId = gap.ResourceId,
            Outcome = AuditOutcome.Success,
            Details = gap.ToDetails(),
        };

        return bare with { EntryHash = keyed ? MacHash(bare) : LegacyHash(bare) };
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
