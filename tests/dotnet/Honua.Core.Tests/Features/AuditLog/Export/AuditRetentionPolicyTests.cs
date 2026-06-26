// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Core.Tests.Features.AuditLog.Export;

/// <summary>
/// Unit tests for <see cref="AuditRetentionPolicy"/> cutoff arithmetic (#2157).
/// </summary>
public sealed class AuditRetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsExpired_EventOlderThanWindow_IsExpired()
    {
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(30) };

        policy.IsBounded.Should().BeTrue();
        policy.IsExpired(Now.AddDays(-31), Now).Should().BeTrue();
    }

    [Fact]
    public void IsExpired_EventWithinWindow_IsRetained()
    {
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(30) };

        policy.IsExpired(Now.AddDays(-29), Now).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_ZeroWindow_RetainsForever()
    {
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.Zero };

        policy.IsBounded.Should().BeFalse();
        policy.IsExpired(Now.AddYears(-10), Now).Should().BeFalse();
    }

    [Fact]
    public void CutoffUtc_Unbounded_IsMinValue()
    {
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromSeconds(-5) };

        policy.CutoffUtc(Now).Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void CutoffUtc_Bounded_SubtractsWindow()
    {
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(7) };

        policy.CutoffUtc(Now).Should().Be(Now.AddDays(-7).ToUniversalTime());
    }
}

/// <summary>
/// Unit tests for the in-memory retention pruner seam (#2157).
/// </summary>
public sealed class InMemoryAuditRetentionPrunerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PruneAsync_RemovesExpiredAndKeepsRetainedInOrder()
    {
        var old1 = AuditExportTestData.SampleEvent(Now.AddDays(-40), action: "old.1");
        var keep1 = AuditExportTestData.SampleEvent(Now.AddDays(-5), action: "keep.1");
        var old2 = AuditExportTestData.SampleEvent(Now.AddDays(-35), action: "old.2");
        var keep2 = AuditExportTestData.SampleEvent(Now.AddDays(-1), action: "keep.2");

        var pruner = new InMemoryAuditRetentionPruner(new[] { old1, keep1, old2, keep2 }, () => Now);
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.FromDays(30) };

        var pruned = await pruner.PruneAsync(policy, CancellationToken.None);

        pruned.Should().Be(2);
        pruner.Remaining.Select(e => e.Action).Should().Equal("keep.1", "keep.2");
    }

    [Fact]
    public async Task PruneAsync_UnboundedPolicy_PrunesNothing()
    {
        var events = new[]
        {
            AuditExportTestData.SampleEvent(Now.AddYears(-5), action: "ancient"),
        };
        var pruner = new InMemoryAuditRetentionPruner(events, () => Now);
        var policy = new AuditRetentionPolicy { RetentionWindow = TimeSpan.Zero };

        var pruned = await pruner.PruneAsync(policy, CancellationToken.None);

        pruned.Should().Be(0);
        pruner.Remaining.Should().HaveCount(1);
    }
}
