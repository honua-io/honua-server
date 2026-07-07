// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Operations.Status;
using Xunit;

namespace Honua.Server.Tests.Features.OperateStatus;

/// <summary>
/// Unit tests for the server-side aggregated-status verdict rules (A12). Pure and deterministic —
/// they pin the documented "is the system healthy" logic under composed conditions so the verdict is
/// never client-invented.
/// </summary>
public sealed class OperateStatusVerdictEvaluatorTests
{
    private static OperateStatusSignals Clear(string rollup = "Healthy")
        => new(
            HealthRollupStatus: rollup,
            CriticalFindingRules: [],
            ParkedDeploys: 0,
            AlertDeadLettered: 0,
            AlertDispatchImpaired: false,
            SloErrorBudgetExhausted: false);

    [Fact]
    public void Evaluate_AllClear_IsHealthy()
    {
        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(Clear());

        status.Should().Be(OperateOverallStatus.Healthy);
        reasons.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_HealthRollupUnhealthy_IsUnhealthy()
    {
        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(Clear("Unhealthy"));

        status.Should().Be(OperateOverallStatus.Unhealthy);
        reasons.Should().Contain("health-rollup:Unhealthy");
    }

    [Fact]
    public void Evaluate_CriticalFinding_IsDegraded()
    {
        var signals = Clear() with { CriticalFindingRules = ["deploy-manual-intervention"] };

        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(signals);

        status.Should().Be(OperateOverallStatus.Degraded);
        reasons.Should().Contain("critical-finding:deploy-manual-intervention");
    }

    [Fact]
    public void Evaluate_ParkedDeploy_IsDegraded()
    {
        var signals = Clear() with { ParkedDeploys = 1 };

        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(signals);

        status.Should().Be(OperateOverallStatus.Degraded);
        reasons.Should().Contain("deploy-parked");
    }

    [Fact]
    public void Evaluate_HealthRollupDegraded_IsDegraded()
    {
        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(Clear("Degraded"));

        status.Should().Be(OperateOverallStatus.Degraded);
        reasons.Should().Contain("health-rollup:Degraded");
    }

    [Fact]
    public void Evaluate_AlertDeadLettered_IsDegraded()
    {
        var signals = Clear() with { AlertDeadLettered = 3 };

        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(signals);

        status.Should().Be(OperateOverallStatus.Degraded);
        reasons.Should().Contain("alert-dead-lettered");
    }

    [Fact]
    public void Evaluate_SloErrorBudgetExhausted_IsDegraded()
    {
        var signals = Clear() with { SloErrorBudgetExhausted = true };

        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(signals);

        status.Should().Be(OperateOverallStatus.Degraded);
        reasons.Should().Contain("slo-error-budget-exhausted");
    }

    [Fact]
    public void Evaluate_UnhealthyDominatesOtherDegradations()
    {
        var signals = Clear("Unhealthy") with
        {
            CriticalFindingRules = ["deploy-manual-intervention"],
            ParkedDeploys = 2,
        };

        var (status, _) = OperateStatusVerdictEvaluator.Evaluate(signals);

        status.Should().Be(OperateOverallStatus.Unhealthy);
    }

    [Theory]
    [InlineData(OperateOverallStatus.Healthy, "healthy")]
    [InlineData(OperateOverallStatus.Degraded, "degraded")]
    [InlineData(OperateOverallStatus.Unhealthy, "unhealthy")]
    public void ToWire_MapsToLowercaseToken(OperateOverallStatus status, string expected)
        => OperateStatusVerdictEvaluator.ToWire(status).Should().Be(expected);
}
