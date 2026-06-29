// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// Pure, deterministic evaluator that decides whether an active-passive failover should be
/// triggered. It counts the trailing run of consecutive failed health checks against the
/// primary and, once that run crosses the configured threshold, gates the failover on the
/// standby being recoverable inside its objectives. Keeping the failover decision in Core
/// (independent of any orchestration or health-probe backend) lets the failover automation,
/// the admin reporting surface, and tests share one definition of "should we fail over?"
/// (#356, ADR-0024).
/// </summary>
public static class FailoverDecisionEvaluator
{
    /// <summary>
    /// Evaluates the failover decision as of <paramref name="asOf"/>.
    /// </summary>
    /// <param name="policy">The failover policy to apply.</param>
    /// <param name="primaryHealthChecks">
    /// Health checks against the primary in chronological order (oldest first). The trailing
    /// run of unhealthy checks is what counts toward the failover threshold; an empty series
    /// is treated as zero failures.
    /// </param>
    /// <param name="standbyReadiness">The standby's current recovery readiness.</param>
    /// <param name="asOf">The reference time the assessment is stamped with.</param>
    /// <returns>A failover assessment.</returns>
    /// <remarks>
    /// Decision rules:
    /// <list type="bullet">
    ///   <item><description>
    ///     Fewer trailing consecutive failures than the threshold →
    ///     <see cref="FailoverDecision.Hold"/>; stay on the primary.
    ///   </description></item>
    ///   <item><description>
    ///     Threshold reached and the standby is recoverable (or the policy does not require it)
    ///     → <see cref="FailoverDecision.Promote"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Threshold reached but the policy requires a recoverable standby and the standby is
    ///     not <see cref="RecoveryReadinessLevel.Ready"/> → <see cref="FailoverDecision.Block"/>;
    ///     promoting would risk breaching the recovery point objective.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static FailoverAssessment Evaluate(
        FailoverPolicy policy,
        IReadOnlyList<HealthSample> primaryHealthChecks,
        RecoveryReadiness standbyReadiness,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(primaryHealthChecks);
        ArgumentNullException.ThrowIfNull(standbyReadiness);

        var consecutiveFailures = 0;
        for (var i = primaryHealthChecks.Count - 1; i >= 0; i--)
        {
            if (primaryHealthChecks[i].Healthy)
            {
                break;
            }

            consecutiveFailures++;
        }

        var standbyReady = standbyReadiness.Level == RecoveryReadinessLevel.Ready;
        var thresholdReached = consecutiveFailures >= policy.FailureThreshold;

        FailoverDecision decision;
        string reason;
        if (!thresholdReached)
        {
            decision = FailoverDecision.Hold;
            reason = consecutiveFailures == 0
                ? "Primary is healthy; holding."
                : $"Primary has {consecutiveFailures} consecutive failed health check(s), below the threshold of {policy.FailureThreshold}; holding.";
        }
        else if (!policy.RequireStandbyRecoveryReady || standbyReady)
        {
            decision = FailoverDecision.Promote;
            reason = $"Primary failed {consecutiveFailures} consecutive health check(s), reaching the threshold of {policy.FailureThreshold}; standby is recoverable, promoting.";
        }
        else
        {
            decision = FailoverDecision.Block;
            reason = $"Primary failed {consecutiveFailures} consecutive health check(s), reaching the threshold of {policy.FailureThreshold}, but the standby is not recoverable inside its objectives ({standbyReadiness.Level}); blocking automated failover pending operator action.";
        }

        return new FailoverAssessment(
            Decision: decision,
            ConsecutiveFailures: consecutiveFailures,
            FailureThreshold: policy.FailureThreshold,
            StandbyRecoveryReady: standbyReady,
            AssessedAt: asOf,
            Reason: reason);
    }
}
