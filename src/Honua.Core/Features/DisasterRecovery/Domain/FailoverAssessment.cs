// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// A point-in-time assessment of an active-passive failover playbook, derived from the recent
/// health-check history of the primary and the standby's recovery readiness. This is the
/// projection a failover orchestrator or the admin observability surface acts on (#356).
/// </summary>
/// <param name="Decision">The recommended failover action.</param>
/// <param name="ConsecutiveFailures">The number of trailing consecutive failed health checks against the primary.</param>
/// <param name="FailureThreshold">The configured failover threshold the failures were measured against.</param>
/// <param name="StandbyRecoveryReady">Whether the standby was recoverable inside its objectives at assessment time.</param>
/// <param name="AssessedAt">When the assessment was computed.</param>
/// <param name="Reason">Operator-facing explanation of why the <paramref name="Decision"/> was reached.</param>
public sealed record FailoverAssessment(
    FailoverDecision Decision,
    int ConsecutiveFailures,
    int FailureThreshold,
    bool StandbyRecoveryReady,
    DateTimeOffset AssessedAt,
    string Reason);
