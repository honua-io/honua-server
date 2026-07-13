// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Alerts;

/// <summary>
/// Exposes the alert-evaluation loop's liveness so the evaluation health check can fault on a
/// dead or hung loop (#2810). Implemented by <see cref="AlertEvaluationBackgroundService"/>,
/// which stamps a heartbeat on every iteration (leader or not) so the check distinguishes a
/// loop that has stopped iterating from one that is simply not the current leader.
/// </summary>
/// <remarks>
/// This surfaces only the local node's loop state. Fleet-wide "no node holds the evaluation
/// lease" detection is a separate concern handled by the <c>alert-evaluation-no-leader</c>
/// ops finding, which reads the shared checkpoint heartbeat rather than any single node's view.
/// </remarks>
internal interface IAlertEvaluationHealth
{
    /// <summary>True once the evaluation loop is running (false when disabled or exited).</summary>
    bool IsEvaluatorRunning { get; }

    /// <summary>True when the alert pipeline is enabled by configuration.</summary>
    bool IsEvaluatorEnabled { get; }

    /// <summary>True when this node currently holds the evaluation leader lease.</summary>
    bool IsLeader { get; }

    /// <summary>
    /// Timestamp of the loop's most recent iteration (heartbeat). Advances every idle poll on
    /// every node whether or not it is the leader, so staleness means the loop is hung — not
    /// merely idle or a non-leader. Null before the first iteration.
    /// </summary>
    DateTimeOffset? LastPollAt { get; }

    /// <summary>
    /// Timestamp the loop began running on this node, or null when it has not started. Used by
    /// the no-leader finding to distinguish a genuinely leaderless cluster from a cold start
    /// that simply has not swept yet.
    /// </summary>
    DateTimeOffset? RunningSince { get; }
}
