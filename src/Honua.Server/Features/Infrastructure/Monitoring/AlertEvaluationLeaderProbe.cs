// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Fleet-wide liveness seam for the alert-evaluation lease, consumed by the
/// <c>alert-evaluation-no-leader</c> ops finding (#2810). Combines this node's local loop state
/// (<see cref="IAlertEvaluationHealth"/>) with the <b>shared</b> checkpoint heartbeat — the leader
/// stamps <see cref="AlertWorkerCheckpoint.LastDwellSweepAt"/> into the durable checkpoint every
/// dwell sweep, so any node can read it to tell whether <i>some</i> node is holding the lease and
/// making progress, without depending on its own view of leadership.
/// </summary>
internal sealed class AlertEvaluationLeaderProbe
{
    private readonly IAlertEvaluationHealth _health;
    private readonly IAlertCheckpointStore _checkpointStore;
    private readonly string _workerName;

    public AlertEvaluationLeaderProbe(
        IAlertEvaluationHealth health,
        IAlertCheckpointStore checkpointStore,
        IOptions<AlertOptions> options)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        var evaluation = (options ?? throw new ArgumentNullException(nameof(options))).Value.Evaluation;
        _workerName = evaluation.WorkerName;
        NoLeaderStaleAfter = evaluation.NoLeaderStaleAfter;
    }

    /// <summary>
    /// Duration since the last leader progress beyond which the cluster is treated as leaderless.
    /// </summary>
    public TimeSpan NoLeaderStaleAfter { get; }

    /// <summary>True when the alert evaluator is enabled by configuration.</summary>
    public bool IsEvaluatorEnabled => _health.IsEvaluatorEnabled;

    /// <summary>Timestamp the local evaluation loop began running, or null when it has not started.</summary>
    public DateTimeOffset? RunningSince => _health.RunningSince;

    /// <summary>
    /// Reads the shared checkpoint's last leader-progress timestamp (dwell sweep). Null means no
    /// leader has ever swept — a cold cluster, or one that has never acquired the lease.
    /// </summary>
    public async Task<DateTimeOffset?> GetLastLeaderHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var checkpoint = await _checkpointStore.GetAsync(_workerName, cancellationToken).ConfigureAwait(false);
        return checkpoint.LastDwellSweepAt;
    }
}
