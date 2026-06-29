// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// The action an active-passive failover playbook should take, derived from automated health
/// checks against the primary serving surface and the standby's recovery readiness. The
/// decision is the contract a failover orchestrator (state machine, runbook automation, or an
/// operator) acts on (#356, ADR-0024).
/// </summary>
public enum FailoverDecision
{
    /// <summary>
    /// Stay on the primary. The primary is healthy, or it has not yet failed enough
    /// consecutive health checks to cross the failover threshold.
    /// </summary>
    [JsonStringEnumMemberName("hold")]
    Hold = 0,

    /// <summary>
    /// Promote the standby. The primary has crossed the failover threshold and the standby is
    /// recoverable inside its objectives, so an active-passive failover can proceed safely.
    /// </summary>
    [JsonStringEnumMemberName("promote")]
    Promote = 1,

    /// <summary>
    /// Block automated failover. The primary has crossed the failover threshold but the
    /// standby is not recoverable inside its objectives, so promoting it would risk data loss
    /// beyond the recovery point objective. This requires operator intervention.
    /// </summary>
    [JsonStringEnumMemberName("block")]
    Block = 2
}
