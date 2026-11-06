// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

// A stale follower is not a fault; only a hung leader or a fleet that cannot acquire the lease is unhealthy, and a disabled loop is healthy.
internal sealed class AlertEvaluationHealthCheck : IHealthCheck
{
    private readonly IAlertEvaluationHealth _evaluationHealth;
    private readonly AlertOptions _options;
    private readonly TimeProvider _time;

    public AlertEvaluationHealthCheck(
        IAlertEvaluationHealth evaluationHealth,
        IOptions<AlertOptions> options,
        TimeProvider? time = null)
    {
        _evaluationHealth = evaluationHealth ?? throw new ArgumentNullException(nameof(evaluationHealth));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(Evaluate(_time.GetUtcNow()));

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
