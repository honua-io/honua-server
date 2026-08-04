// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Streaming.Conformance;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for conformance-run leasing, ownership tokens, budgets, and TTL reclamation
/// (honua-server#3038, REQ-005/REQ-006/NFR-001).
/// </summary>
public sealed class FeatureStreamConformanceRunRegistryTests
{
    private const string TestRevision = "0123456789abcdef0123456789abcdef01234567";

    [UnitTest]
    public void TryLease_WhenAllLeasesHeld_ReturnsNull()
    {
        var registry = CreateRegistry(new FakeTimeProvider(), maxConcurrentRuns: 1);

        registry.TryLease("first", TimeSpan.FromMinutes(5), TestRevision).Should().NotBeNull();
        registry.TryLease("second", TimeSpan.FromMinutes(5), TestRevision).Should().BeNull(
            "a run that cannot be isolated must be refused, never given a shared source anyway");
    }

    [UnitTest]
    public void Resolve_WithAnotherRunsToken_ReturnsNull()
    {
        var registry = CreateRegistry(new FakeTimeProvider(), maxConcurrentRuns: 2);

        var first = registry.TryLease("first", TimeSpan.FromMinutes(5), TestRevision)!;
        var second = registry.TryLease("second", TimeSpan.FromMinutes(5), TestRevision)!;

        first.Token.Should().NotBe(second.Token);
        registry.Resolve(first.RunId, second.Token).Should().BeNull();
        registry.Resolve(second.RunId, first.Token).Should().BeNull();
        registry.Resolve(first.RunId, first.Token).Should().BeSameAs(first);
    }

    [UnitTest]
    public void Resolve_AfterTtlElapsed_ReturnsNull()
    {
        var time = new FakeTimeProvider();
        var registry = CreateRegistry(time, maxConcurrentRuns: 1);

        var run = registry.TryLease("expiring", TimeSpan.FromMinutes(5), TestRevision)!;
        registry.Resolve(run.RunId, run.Token).Should().NotBeNull();

        time.Advance(TimeSpan.FromMinutes(5));

        registry.Resolve(run.RunId, run.Token).Should().BeNull();
        registry.IsLeased(run.RunId).Should().BeFalse();
    }

    [UnitTest]
    public void TryLease_AfterExpiredLease_ReclaimsTheSlot()
    {
        var time = new FakeTimeProvider();
        var registry = CreateRegistry(time, maxConcurrentRuns: 1);

        var abandoned = registry.TryLease("abandoned", TimeSpan.FromMinutes(5), TestRevision)!;
        time.Advance(TimeSpan.FromMinutes(6));

        var replacement = registry.TryLease("replacement", TimeSpan.FromMinutes(5), TestRevision);

        replacement.Should().NotBeNull("an expired lease must not permanently block the next run");
        replacement!.RunId.Should().NotBe(abandoned.RunId);
    }

    [UnitTest]
    public void ReclaimExpired_ReturnsOnlyExpiredRuns()
    {
        var time = new FakeTimeProvider();
        var registry = CreateRegistry(time, maxConcurrentRuns: 4);

        var shortRun = registry.TryLease("short", TimeSpan.FromMinutes(1), TestRevision)!;
        var longRun = registry.TryLease("long", TimeSpan.FromMinutes(10), TestRevision)!;

        time.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = registry.ReclaimExpired();

        reclaimed.Should().ContainSingle().Which.RunId.Should().Be(shortRun.RunId);
        registry.IsLeased(longRun.RunId).Should().BeTrue();
    }

    [UnitTest]
    public void TryClaimMutation_EnforcesMutationAndRecordBudgets()
    {
        var registry = CreateRegistry(
            new FakeTimeProvider(),
            maxConcurrentRuns: 1,
            maxMutationsPerRun: 3,
            maxRecordsPerRun: 1);

        var run = registry.TryLease("bounded", TimeSpan.FromMinutes(5), TestRevision)!;

        run.TryClaimMutation(createsRecord: true).Should().Be(1);
        run.TrackRecord(42);

        // The record budget is spent, so a second creating mutation is refused even though the
        // mutation budget still has room.
        run.TryClaimMutation(createsRecord: true).Should().BeNull();

        run.TryClaimMutation(createsRecord: false).Should().Be(2);
        run.TryClaimMutation(createsRecord: false).Should().Be(3);
        run.TryClaimMutation(createsRecord: false).Should().BeNull("the mutation budget is spent");
    }

    [UnitTest]
    public void ReleaseClaim_ReturnsAnUnspentClaimToTheBudget()
    {
        var registry = CreateRegistry(new FakeTimeProvider(), maxConcurrentRuns: 1, maxMutationsPerRun: 1);
        var run = registry.TryLease("retry", TimeSpan.FromMinutes(5), TestRevision)!;

        run.TryClaimMutation(createsRecord: false).Should().Be(1);
        run.ReleaseClaim();

        run.TryClaimMutation(createsRecord: false).Should().Be(1,
            "a mutation that never reached storage must not consume the run's budget");
    }

    [UnitTest]
    public void Release_IsIdempotent()
    {
        var registry = CreateRegistry(new FakeTimeProvider(), maxConcurrentRuns: 1);
        var run = registry.TryLease("released", TimeSpan.FromMinutes(5), TestRevision)!;

        registry.Release(run.RunId);
        registry.Release(run.RunId);

        registry.ActiveRunCount.Should().Be(0);
    }

    [UnitTest]
    public void DescribeCapability_WhenDisabled_AdvertisesNothingButTheAnswer()
    {
        var registry = new FeatureStreamConformanceRunRegistry(
            Options.Create(new FeatureStreamConformanceOptions { Enabled = false }),
            new FakeTimeProvider());

        var capability = registry.DescribeCapability();

        capability.Enabled.Should().BeFalse();
        capability.ServiceId.Should().BeNull("a disabled deployment must not name a source it will not mutate");
        capability.Operations.Should().BeNull();
    }

    [UnitTest]
    public void Marker_RoundTripsRunIdentityAndDeadline()
    {
        var runId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

        var formatted = new FeatureStreamConformanceMarker(runId, expiresAt).Format();

        FeatureStreamConformanceMarker.TryParse(formatted, out var parsed).Should().BeTrue();
        parsed.RunId.Should().Be(runId);
        parsed.ExpiresAt.Should().Be(expiresAt);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Honolulu")]
    [InlineData("honua-conformance")]
    [InlineData("honua-conformance:not-a-guid:1900000000")]
    [InlineData("other-owner:0123456789abcdef0123456789abcdef:1900000000")]
    public void Marker_RejectsValuesThisServerDidNotWrite(string? stored)
    {
        // Anything unparseable is a baseline record as far as the sweeper is concerned, which
        // is what keeps a sweep from deleting data it does not own.
        FeatureStreamConformanceMarker.TryParse(stored, out _).Should().BeFalse();
    }

    /// <summary>
    /// Minimal controllable clock, mirroring the local <c>FakeTimeProvider</c> convention used
    /// elsewhere in this suite rather than taking an external test-time package dependency.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private static FeatureStreamConformanceRunRegistry CreateRegistry(
        TimeProvider timeProvider,
        int maxConcurrentRuns,
        int maxMutationsPerRun = 32,
        int maxRecordsPerRun = 8)
        => new(
            Options.Create(new FeatureStreamConformanceOptions
            {
                Enabled = true,
                ServiceId = "conformance",
                LayerId = 0,
                MaxConcurrentRuns = maxConcurrentRuns,
                MaxMutationsPerRun = maxMutationsPerRun,
                MaxRecordsPerRun = maxRecordsPerRun
            }),
            timeProvider);
}
