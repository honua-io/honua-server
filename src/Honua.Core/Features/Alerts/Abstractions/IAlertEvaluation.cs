// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Evaluates feature transitions for a specific alert rule.
/// </summary>
public interface IAlertEvaluator
{
    /// <summary>
    /// Evaluates a change event for a rule and returns updated state plus generated events.
    /// </summary>
    /// <param name="change">Durable feature change</param>
    /// <param name="feature">Current feature snapshot; null when deleted</param>
    /// <param name="rule">Alert rule definition</param>
    /// <param name="zone">Optional geofence zone associated with the rule</param>
    /// <param name="currentState">Current persisted state, if any</param>
    /// <param name="evaluatedAt">Evaluation timestamp</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Evaluation result with state and events</returns>
    Task<AlertEvaluationResult> EvaluateAsync(
        AlertChange change,
        Feature? feature,
        AlertRuleDefinition rule,
        AlertZoneDefinition? zone,
        AlertStateSnapshot? currentState,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies edition-based access policies for alert features.
/// </summary>
public interface IAlertEditionPolicy
{
    /// <summary>
    /// Returns true when the active edition permits rule creation/execution. This requires the
    /// trigger's entitlement, the alert-evaluation engine entitlement, and a rule tier that fits
    /// within the effective edition.
    /// </summary>
    /// <param name="rule">Rule to validate</param>
    /// <returns>True when the active edition can use the rule</returns>
    bool IsRuleAllowed(AlertRuleDefinition rule);

    /// <summary>
    /// Returns true when the active edition permits the specified trigger type on its own,
    /// independent of the evaluation-engine entitlement and of any rule's self-declared tier.
    /// Admin surfaces use this to attribute an entitlement denial to the exact key that is
    /// missing instead of blaming the trigger for an unrelated gap.
    /// </summary>
    /// <param name="triggerType">Trigger type to check</param>
    /// <returns>True when the trigger's entitlement key is active and within the edition cap</returns>
    bool IsTriggerAllowed(AlertTriggerType triggerType);

    /// <summary>
    /// Returns true when the active edition permits the specified channel.
    /// </summary>
    /// <param name="channelType">Delivery channel</param>
    /// <returns>True when the channel is allowed</returns>
    bool IsChannelAllowed(AlertChannelType channelType);

    /// <summary>
    /// Returns true when the current server configuration can deliver the specified channel.
    /// </summary>
    /// <param name="channelType">Delivery channel</param>
    /// <returns>True when the channel is configured for delivery</returns>
    bool IsChannelConfigured(AlertChannelType channelType);

    /// <summary>
    /// Explains why an <c>alerts.*</c>/<c>channels.*</c> entitlement key is not permitted:
    /// the license does not carry it, or the downward-only <c>Alerts:Edition</c> cap excludes
    /// the tier it belongs to. Admin surfaces use this so a cap-only denial is not reported as
    /// a missing entitlement (#2998).
    /// </summary>
    /// <param name="entitlementKey">An <c>alerts.*</c> or <c>channels.*</c> entitlement key.</param>
    /// <returns>The denial reason, or <see cref="AlertEditionDenialReason.None"/> when permitted.</returns>
    AlertEditionDenialReason GetEntitlementDenialReason(string entitlementKey);

    /// <summary>
    /// Explains why a delivery channel is not permitted, including channels with no catalog key
    /// of their own (WebSocket), which are gated by the effective edition rather than a key.
    /// </summary>
    /// <param name="channelType">Delivery channel</param>
    /// <returns>The denial reason, or <see cref="AlertEditionDenialReason.None"/> when permitted.</returns>
    AlertEditionDenialReason GetChannelDenialReason(AlertChannelType channelType);
}

/// <summary>
/// Provides singleton leadership for evaluator workers.
/// </summary>
public interface ILeaderElectionStrategy
{
    /// <summary>
    /// Attempts to acquire or renew leadership.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True when this instance is leader</returns>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases leadership.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Indicates whether the current instance is leader.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Indicates whether the most recent <see cref="TryAcquireAsync"/> attempt <b>faulted</b> —
    /// the coordinator could not determine leadership because the attempt itself errored (for
    /// example a connection-pool exhaustion or database outage), as opposed to cleanly observing
    /// that another instance holds the lease. A healthy follower that simply lost the race reports
    /// <c>false</c>. This lets the evaluation loop distinguish "someone else leads" (healthy) from
    /// "nobody can acquire" (a fleet-wide stall) so a no-leader fault can be surfaced.
    /// </summary>
    bool LastAcquireFaulted { get; }

    /// <summary>
    /// Unique instance identifier used for diagnostics.
    /// </summary>
    string InstanceId { get; }
}
