// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Operations.Status;
using Honua.ServiceDefaults;
using Xunit;

namespace Honua.Server.Tests.Features.OperateStatus;

/// <summary>
/// Unit tests for the minimal v1 availability SLO / error-budget evaluation (A12). Deterministic —
/// exercises what the contract actually computes from the in-process serving-latency window, and the
/// explicit not-configured state.
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
    public void Evaluate_NoTarget_ReportsNotConfigured()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = null } };

        var view = OperateSloEvaluator.Evaluate(options, Snapshot(100, 0));

        view.Configured.Should().BeFalse();
        view.Reason.Should().NotBeNullOrWhiteSpace();
        view.Availability.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ConfiguredWithHealthyTraffic_ComputesBudgetRemaining()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.99 } };

        // 1000 requests, 1 error => error fraction 0.001; budget 0.01; burn 0.1; remaining 0.9.
        var view = OperateSloEvaluator.Evaluate(options, Snapshot(1000, 1));

        view.Configured.Should().BeTrue();
        view.Availability.Should().NotBeNull();
        view.Availability!.Target.Should().Be(0.99);
        view.Availability.RequestCount.Should().Be(1000);
        view.Availability.ErrorCount.Should().Be(1);
        view.Availability.Observed.Should().BeApproximately(0.999, 1e-9);
        view.Availability.BurnRate.Should().BeApproximately(0.1, 1e-9);
        view.Availability.ErrorBudgetRemaining.Should().BeApproximately(0.9, 1e-9);
        OperateSloEvaluator.IsErrorBudgetExhausted(view).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ConfiguredWithNoTraffic_LeavesEvaluationNull()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.995 } };

        var view = OperateSloEvaluator.Evaluate(options, Snapshot(0, 0));

        view.Configured.Should().BeTrue();
        view.Availability.Should().NotBeNull();
        view.Availability!.Observed.Should().BeNull();
        view.Availability.BurnRate.Should().BeNull();
        view.Availability.ErrorBudgetRemaining.Should().BeNull();
        OperateSloEvaluator.IsErrorBudgetExhausted(view).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_BudgetExhausted_IsDetected()
    {
        var options = new OperateSloOptions { Availability = new AvailabilityOptions { Target = 0.99 } };

        // 100 requests, 5 errors => error fraction 0.05; budget 0.01; burn 5.0; remaining clamped to 0.
        var view = OperateSloEvaluator.Evaluate(options, Snapshot(100, 5));

        view.Availability!.ErrorBudgetRemaining.Should().Be(0.0);
        view.Availability.BurnRate.Should().BeApproximately(5.0, 1e-9);
        OperateSloEvaluator.IsErrorBudgetExhausted(view).Should().BeTrue();
    }
}
