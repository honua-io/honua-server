// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// Surfaces the alert <b>evaluation</b> loop's liveness through the <see cref="HealthCheckService"/>
/// roll-up (and, via <c>ReadinessCheckService</c>, the paged readiness path). The evaluator is the
/// half of the alert pipeline with no prior health coverage: leader election handles crash failover,
/// but a hung leader or a fleet-wide inability to acquire the lease (the "no-leader" stall) left
/// evaluation silently halted. This check reports:
/// <list type="bullet">
///   <item><b>Healthy</b> when disabled (an operating choice, not a fault), or when the loop is
///   running and either following a healthy leader or leading with a fresh heartbeat;</item>
///   <item><b>Unhealthy</b> when the loop is not running, when no node can acquire the lease beyond
///   <see cref="AlertEvaluationOptions.NoLeaderThreshold"/>, or when this node leads but its last
///   productive pass has aged past <see cref="AlertEvaluationOptions.HeartbeatStalenessThreshold"/>.</item>
/// </list>
/// A follower whose <c>LastLeaderPassAt</c> is stale is <i>not</i> a fault — only the current leader's
/// heartbeat staleness matters, so idle followers never trip the check.
/// </summary>
internal sealed class AlertEvaluationHealthCheck : IHealthCheck
{
    private readonly IAlertEvaluationHealth _evaluationHealth;
    private readonly AlertOptions _options;

    public AlertEvaluationHealthCheck(
        IAlertEvaluationHealth evaluationHealth,
        IOptions<AlertOptions> options)
    {
        _evaluationHealth = evaluationHealth ?? throw new ArgumentNullException(nameof(evaluationHealth));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(Evaluate(DateTimeOffset.UtcNow));

    internal HealthCheckResult Evaluate(DateTimeOffset now)
    {
        if (!_evaluationHealth.IsEvaluatorEnabled)
        {
            return HealthCheckResult.Healthy(
                "Alert evaluator is disabled (Alerts:Enabled=false); no evaluation loop to report.");
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["evaluator_running"] = _evaluationHealth.IsEvaluatorRunning,
            ["is_leader"] = _evaluationHealth.IsLeader,
        };

        if (_evaluationHealth.LastHeartbeatAt is { } heartbeatAt)
        {
            data["last_heartbeat_at"] = heartbeatAt.ToString("o", CultureInfo.InvariantCulture);
        }

        if (_evaluationHealth.LastLeaderPassAt is { } leaderPassAt)
        {
            data["last_leader_pass_at"] = leaderPassAt.ToString("o", CultureInfo.InvariantCulture);
        }

        // A loop that never started (or exited) evaluates nothing; report Unhealthy.
        if (!_evaluationHealth.IsEvaluatorRunning)
        {
            return HealthCheckResult.Unhealthy(
                "Alert evaluator loop is not running; no alert rules are being evaluated until it restarts.",
                data: data);
        }

        // No-leader stall: every recent acquisition attempt faulted (the coordinator errored) for
        // longer than the threshold, so evaluation has halted fleet-wide with no leader.
        if (_evaluationHealth.LeaderAcquisitionFailingSince is { } failingSince
            && now - failingSince >= _options.Evaluation.NoLeaderThreshold)
        {
            data["leader_acquisition_failing_since"] = failingSince.ToString("o", CultureInfo.InvariantCulture);
            return HealthCheckResult.Unhealthy(
                "No node can acquire the alert-evaluation lease; evaluation is stalled fleet-wide (no leader).",
                data: data);
        }

        // Hung leader: this node holds leadership but its last productive pass is stale.
        if (_evaluationHealth.IsLeader
            && _evaluationHealth.LastLeaderPassAt is { } lastPass
            && now - lastPass >= _options.Evaluation.HeartbeatStalenessThreshold)
        {
            return HealthCheckResult.Unhealthy(
                $"Alert-evaluation leader heartbeat is stale (last productive pass {lastPass.ToString("o", CultureInfo.InvariantCulture)}); the loop appears hung.",
                data: data);
        }

        return HealthCheckResult.Healthy("Alert evaluator is healthy.", data);
    }
}
