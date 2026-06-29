// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.DisasterRecovery.Domain;

namespace Honua.Core.Tests.Features.DisasterRecovery;

/// <summary>
/// Unit tests for the active-passive failover decision evaluator (#356). These assert the
/// shared failover-trigger rules the failover automation and admin reporting surface depend on.
/// </summary>
public sealed class FailoverDecisionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    private static RecoveryReadiness Readiness(RecoveryReadinessLevel level) => new(
        Level: level,
        Objectives: RecoveryObjectives.Default,
        LastSuccessfulBaseBackup: Now.AddMinutes(-10),
        RestorablePoint: Now.AddMinutes(-1),
        DataLossWindow: TimeSpan.FromMinutes(1),
        RecoveryPointObjectiveMet: level == RecoveryReadinessLevel.Ready,
        AssessedAt: Now);

    private static HealthSample Healthy(int minutesAgo) =>
        new(Now.AddMinutes(-minutesAgo), Healthy: true);

    private static HealthSample Unhealthy(int minutesAgo) =>
        new(Now.AddMinutes(-minutesAgo), Healthy: false, Reason: "timeout");

    [Fact]
    public void Evaluate_HealthyPrimary_Holds()
    {
        var checks = new[] { Healthy(3), Healthy(2), Healthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, checks, Readiness(RecoveryReadinessLevel.Ready), Now);

        assessment.Decision.Should().Be(FailoverDecision.Hold);
        assessment.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void Evaluate_NoChecks_Holds()
    {
        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, [], Readiness(RecoveryReadinessLevel.Ready), Now);

        assessment.Decision.Should().Be(FailoverDecision.Hold);
        assessment.ConsecutiveFailures.Should().Be(0);
        assessment.AssessedAt.Should().Be(Now);
    }

    [Fact]
    public void Evaluate_FailuresBelowThreshold_Holds()
    {
        // Two trailing failures with a threshold of three stays on the primary.
        var checks = new[] { Healthy(3), Unhealthy(2), Unhealthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, checks, Readiness(RecoveryReadinessLevel.Ready), Now);

        assessment.Decision.Should().Be(FailoverDecision.Hold);
        assessment.ConsecutiveFailures.Should().Be(2);
        assessment.FailureThreshold.Should().Be(3);
    }

    [Fact]
    public void Evaluate_ThresholdReachedAndStandbyReady_Promotes()
    {
        var checks = new[] { Unhealthy(3), Unhealthy(2), Unhealthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, checks, Readiness(RecoveryReadinessLevel.Ready), Now);

        assessment.Decision.Should().Be(FailoverDecision.Promote);
        assessment.ConsecutiveFailures.Should().Be(3);
        assessment.StandbyRecoveryReady.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ThresholdReachedButStandbyNotReady_Blocks()
    {
        var checks = new[] { Unhealthy(3), Unhealthy(2), Unhealthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, checks, Readiness(RecoveryReadinessLevel.AtRisk), Now);

        assessment.Decision.Should().Be(FailoverDecision.Block);
        assessment.StandbyRecoveryReady.Should().BeFalse();
        assessment.Reason.Should().Contain("not recoverable");
    }

    [Fact]
    public void Evaluate_ThresholdReachedNotReadyButPolicyAllows_Promotes()
    {
        // When the policy does not require a recoverable standby, failover proceeds regardless.
        var policy = new FailoverPolicy(failureThreshold: 3, requireStandbyRecoveryReady: false);
        var checks = new[] { Unhealthy(3), Unhealthy(2), Unhealthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            policy, checks, Readiness(RecoveryReadinessLevel.NotReady), Now);

        assessment.Decision.Should().Be(FailoverDecision.Promote);
    }

    [Fact]
    public void Evaluate_RecoveredAfterFailures_ResetsConsecutiveCount()
    {
        // A trailing healthy check breaks the failure run even after earlier failures.
        var checks = new[] { Unhealthy(4), Unhealthy(3), Unhealthy(2), Healthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, checks, Readiness(RecoveryReadinessLevel.Ready), Now);

        assessment.Decision.Should().Be(FailoverDecision.Hold);
        assessment.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void Evaluate_CountsOnlyTrailingFailures()
    {
        // Earlier failures separated by a healthy check do not count toward the trailing run.
        var checks = new[] { Unhealthy(5), Healthy(4), Unhealthy(3), Unhealthy(2), Unhealthy(1) };

        var assessment = FailoverDecisionEvaluator.Evaluate(
            FailoverPolicy.Default, checks, Readiness(RecoveryReadinessLevel.Ready), Now);

        assessment.ConsecutiveFailures.Should().Be(3);
        assessment.Decision.Should().Be(FailoverDecision.Promote);
    }

    [Fact]
    public void Evaluate_NullArguments_Throw()
    {
        var ready = Readiness(RecoveryReadinessLevel.Ready);

        var actPolicy = () => FailoverDecisionEvaluator.Evaluate(null!, [], ready, Now);
        var actChecks = () => FailoverDecisionEvaluator.Evaluate(FailoverPolicy.Default, null!, ready, Now);
        var actReadiness = () => FailoverDecisionEvaluator.Evaluate(FailoverPolicy.Default, [], null!, Now);

        actPolicy.Should().Throw<ArgumentNullException>();
        actChecks.Should().Throw<ArgumentNullException>();
        actReadiness.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FailoverPolicy_NonPositiveThreshold_Throws(int threshold)
    {
        var act = () => new FailoverPolicy(threshold);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("failureThreshold");
    }

    [Fact]
    public void FailoverPolicy_Default_RequiresRecoverableStandby()
    {
        FailoverPolicy.Default.FailureThreshold.Should().Be(3);
        FailoverPolicy.Default.RequireStandbyRecoveryReady.Should().BeTrue();
    }
}
