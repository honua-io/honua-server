// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Alerts;

/// <summary>
/// Exposes the alert <b>evaluation</b> loop's liveness to the evaluation health check and the
/// readiness (paged) path. Implemented by <see cref="AlertEvaluationBackgroundService"/>, which
/// refreshes the snapshot every loop pass. Unlike the dispatcher, the evaluator is leader-elected:
/// exactly one node runs productive passes at a time, so the health signal must distinguish a
/// healthy follower from a genuine stall. Two distinct faults are surfaced:
/// <list type="bullet">
///   <item>a <i>hung leader</i> — this node holds leadership but its last productive pass has aged
///   past the staleness threshold; and</item>
///   <item>a <i>no-leader</i> stall — every recent acquisition attempt faulted (the coordinator
///   errored rather than cleanly conceding to another holder), which is how a fleet-wide halt
///   (e.g. connection-pool exhaustion) presents on each node.</item>
/// </list>
/// </summary>
internal interface IAlertEvaluationHealth
{
    /// <summary>True when the alert pipeline (and therefore the evaluator) is enabled by configuration.</summary>
    bool IsEvaluatorEnabled { get; }

    /// <summary>True once the evaluation loop is running (false when disabled or after it exits).</summary>
    bool IsEvaluatorRunning { get; }

    /// <summary>True when this node currently holds the evaluation leadership lease.</summary>
    bool IsLeader { get; }

    /// <summary>
    /// Timestamp of the most recent loop iteration (leader or follower), proving the loop itself is
    /// alive, or <c>null</c> before the first iteration.
    /// </summary>
    DateTimeOffset? LastHeartbeatAt { get; }

    /// <summary>
    /// Timestamp of the most recent <i>productive</i> pass run while holding leadership, or
    /// <c>null</c> if this node has not yet led. Used to detect a hung leader.
    /// </summary>
    DateTimeOffset? LastLeaderPassAt { get; }

    /// <summary>
    /// The instant leadership acquisition began continuously faulting, or <c>null</c> when the most
    /// recent attempt produced a determinate result (led, or cleanly conceded). Used to detect a
    /// fleet-wide no-leader stall once the gap exceeds the configured threshold.
    /// </summary>
    DateTimeOffset? LeaderAcquisitionFailingSince { get; }
}
