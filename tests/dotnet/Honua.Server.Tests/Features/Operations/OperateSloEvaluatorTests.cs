// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Operations.Status;
using Honua.ServiceDefaults;
using Xunit;

namespace Honua.Server.Tests.Features.OperateStatus;

/// <summary>
/// Unit tests for the operate-status SLO posture. The replica-local reservoir is retained as a
/// diagnostic, but must never be projected as platform availability or an error budget.
/// </summary>
public sealed class OperateSloEvaluatorTests
{
    private static ServingLatencySnapshot Snapshot(long requestCount, long errorCount, double windowSeconds = 300)
        => new()
        {
            WindowSeconds = windowSeconds,
            GeneratedAt = DateTimeOffset.UtcNow,
            Protocols =
            [
                new ServingLatencyProtocolSnapshot
                {
                    Protocol = "featureserver",
                    RequestCount = requestCount,
                    ErrorCount = errorCount,
                    ErrorRate = requestCount == 0 ? 0 : (double)errorCount / requestCount,
                    P50Ms = 1,
                    P95Ms = 2,
                    P99Ms = 3,
                    MaxMs = 4,
                },
            ],
        };

    [Fact]
    public void Evaluate_NoTarget_ReportsPlatformSloUnavailableAndLocalDiagnostic()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = null } };

        var view = OperateSloEvaluator.Evaluate(options, Snapshot(100, 0));

        view.Configured.Should().BeFalse();
        view.Reason.Should().NotBeNullOrWhiteSpace();
        view.Availability.Should().BeNull();
        view.NodeLocalRetainedTail.Should().NotBeNull();
        view.NodeLocalRetainedTail!.Scope.Should().Be("replica-local");
        view.NodeLocalRetainedTail.IncludesInBandErrors.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ConfiguredTarget_DoesNotTurnRetainedTailIntoPlatformSlo()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.99 } };

        var view = OperateSloEvaluator.Evaluate(options, Snapshot(1000, 1));

        view.Configured.Should().BeFalse();
        view.Availability.Should().BeNull();
        view.Reason.Should().Contain("distributed");
        view.NodeLocalRetainedTail!.ConfiguredTarget.Should().Be(0.99);
        view.NodeLocalRetainedTail.RetainedRequestCount.Should().Be(1000);
        view.NodeLocalRetainedTail.RetainedHttpServerErrorCount.Should().Be(1);
        view.NodeLocalRetainedTail.RetainedHttpSuccessRatio.Should().BeApproximately(0.999, 1e-9);
    }

    [Fact]
    public void Evaluate_NoTraffic_ReportsEmptyRetainedPopulation()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.995 } };

        var view = OperateSloEvaluator.Evaluate(options, Snapshot(0, 0));

        view.Configured.Should().BeFalse();
        view.Availability.Should().BeNull();
        view.NodeLocalRetainedTail!.RetainedRequestCount.Should().Be(0);
        view.NodeLocalRetainedTail.RetainedHttpSuccessRatio.Should().BeNull();
    }

    [Fact]
    public void Evaluate_LocalErrors_DoNotPublishBurnRateOrBudgetRemaining()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.99 } };

        var view = OperateSloEvaluator.Evaluate(options, Snapshot(100, 5));

        view.Availability.Should().BeNull();
        view.NodeLocalRetainedTail!.RetainedHttpSuccessRatio.Should().BeApproximately(0.95, 1e-9);
        view.NodeLocalRetainedTail.IsPlatformSli.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UnequalOverflowingReplicaTailsAndRestart_NeverClaimsPlatformAvailability()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.99 } };
        var busyReplica = new ServingLatencyAggregator(TimeSpan.FromSeconds(300), 4096, () => 0);
        var quietReplica = new ServingLatencyAggregator(TimeSpan.FromSeconds(300), 4096, () => 0);
        // Known ledger: 5,000 successes then 100 HTTP failures on A; 10 successes on B.
        // The reservoir keeps only 3,996 successes and 100 failures on A.
        for (var i = 0; i < 5100; i++)
        {
            busyReplica.Record("featureserver", 1, i < 5000 ? 200 : 500);
        }

        for (var i = 0; i < 10; i++)
        {
            quietReplica.Record("featureserver", 1, 200);
        }

        var busy = OperateSloEvaluator.Evaluate(options, busyReplica.GetSnapshot());
        var quiet = OperateSloEvaluator.Evaluate(options, quietReplica.GetSnapshot());
        var restarted = OperateSloEvaluator.Evaluate(options,
            new ServingLatencyAggregator(TimeSpan.FromSeconds(300), 4096, () => 0).GetSnapshot());

        busy.NodeLocalRetainedTail.RetainedRequestCount.Should().Be(4096);
        busy.NodeLocalRetainedTail.TotalRecordedSinceReset.Should().Be(5100);
        busy.NodeLocalRetainedTail.OverwrittenSampleCount.Should().Be(1004);
        busy.NodeLocalRetainedTail.RetainedHttpServerErrorCount.Should().Be(100);
        busy.NodeLocalRetainedTail.RetainedHttpSuccessRatio.Should().BeApproximately(3996d / 4096, 1e-12);
        quiet.NodeLocalRetainedTail.RetainedRequestCount.Should().Be(10);
        restarted.NodeLocalRetainedTail.RetainedRequestCount.Should().Be(0);
        foreach (var view in new[] { busy, quiet, restarted })
        {
            view.Availability.Should().BeNull();
            view.Configured.Should().BeFalse();
            view.NodeLocalRetainedTail.IsPlatformSli.Should().BeFalse();
        }
    }
}
