// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// Surfaces the alert-evaluation loop's liveness through the <see cref="HealthCheckService"/>
/// roll-up (and the ops-health snapshot's <c>overallStatus</c>) so operators see a dead or hung
/// evaluation loop before alerts silently stop firing (#2810). Companion to
/// <see cref="AlertDispatchHealthCheck"/>: the evaluator turns feature changes into alert events,
/// the dispatcher delivers them, and both must be watched.
/// </summary>
/// <remarks>
/// This is a per-node loop-liveness check. It compares the loop's heartbeat
/// (<see cref="IAlertEvaluationHealth.LastPollAt"/>) to now so a loop wedged inside a pass — which
/// keeps <c>IsEvaluatorRunning</c> true while doing no work — is caught, not just a clean crash.
/// Fleet-wide "no node holds the lease" detection is handled separately by the
/// <c>alert-evaluation-no-leader</c> ops finding.
/// </remarks>
internal sealed class AlertEvaluationHealthCheck : IHealthCheck
{
    private readonly IAlertEvaluationHealth _evaluationHealth;
    private readonly AlertOptions _options;
    private readonly TimeProvider _timeProvider;

    public AlertEvaluationHealthCheck(
        IAlertEvaluationHealth evaluationHealth,
        IOptions<AlertOptions> options,
        TimeProvider timeProvider)
    {
        _evaluationHealth = evaluationHealth ?? throw new ArgumentNullException(nameof(evaluationHealth));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_evaluationHealth.IsEvaluatorEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Alert evaluator is disabled (Alerts:Enabled=false); no evaluation loop to report."));
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["evaluator_running"] = _evaluationHealth.IsEvaluatorRunning,
            ["is_leader"] = _evaluationHealth.IsLeader,
        };

        if (_evaluationHealth.LastPollAt is { } lastPollAt)
        {
            data["last_poll_at"] = lastPollAt.ToString("o", CultureInfo.InvariantCulture);
        }

        // A loop that never started (or exited) means this node stops turning feature changes
        // into alert events; report Unhealthy so the fault is visible on the roll-up.
        if (!_evaluationHealth.IsEvaluatorRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Alert evaluation loop is not running; feature changes will not be evaluated into alert events until it restarts.",
                data: data));
        }

        // Heartbeat staleness: a loop hung inside a pass keeps IsEvaluatorRunning true while the
        // heartbeat ages. Compare LastPollAt to now so that hang is caught rather than reported Healthy.
        var now = _timeProvider.GetUtcNow();
        var staleAfter = _options.Evaluation.HeartbeatStaleAfter;
        if (staleAfter > TimeSpan.Zero && _evaluationHealth.LastPollAt is { } heartbeat)
        {
            var age = now - heartbeat;
            if (age >= staleAfter)
            {
                data["heartbeat_age_seconds"] = age.TotalSeconds;
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Alert evaluation loop heartbeat is stale ({age.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s old, threshold {staleAfter.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s); the loop appears hung.",
                    data: data));
            }
        }

        return Task.FromResult(HealthCheckResult.Healthy("Alert evaluation loop is healthy.", data));
    }
}
