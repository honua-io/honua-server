// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// Surfaces alert delivery-outbox backlog and dead-letter accumulation to
/// readiness/health endpoints so operators see delivery stalls before they become
/// silent notification loss. Reads the cached snapshot the dispatcher publishes after
/// each pass (no per-probe database query). Modeled on the feature-change outbox
/// health check. Reports Healthy (with a note) when the pipeline is disabled — a
/// disabled dispatcher is an operating choice, not a fault.
/// </summary>
internal sealed class AlertDispatchHealthCheck : IHealthCheck
{
    private readonly IAlertDispatchHealth _dispatcherHealth;
    private readonly AlertOptions _options;

    public AlertDispatchHealthCheck(
        IAlertDispatchHealth dispatcherHealth,
        IOptions<AlertOptions> options)
    {
        _dispatcherHealth = dispatcherHealth ?? throw new ArgumentNullException(nameof(dispatcherHealth));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_dispatcherHealth.IsDispatcherEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Alert dispatcher is disabled (Alerts:Enabled=false); no delivery backlog to report."));
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["dispatcher_running"] = _dispatcherHealth.IsDispatcherRunning,
        };

        if (_dispatcherHealth.LastPollAt is { } lastPollAt)
        {
            data["last_poll_at"] = lastPollAt.ToString("o", CultureInfo.InvariantCulture);
        }

        // A dispatcher that never started (or exited) leaves work unclaimed; report
        // Unhealthy so readiness reflects that pending notifications will not drain.
        if (!_dispatcherHealth.IsDispatcherRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Alert dispatcher is not running; pending notifications will not be delivered until it restarts.",
                data: data));
        }

        var backlog = _dispatcherHealth.LastBacklog;
        if (backlog is not null)
        {
            data["pending_count"] = backlog.PendingCount;
            data["dead_lettered_count"] = backlog.DeadLetteredCount;

            // Dead letters demand operator triage regardless of a transient poll failure.
            if (backlog.DeadLetteredCount >= _options.Dispatch.UnhealthyDeadLetterThreshold)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Alert dispatch has {backlog.DeadLetteredCount.ToString(CultureInfo.InvariantCulture)} dead-lettered rows requiring operator triage.",
                    data: data));
            }
        }

        if (_dispatcherHealth.IsStoragePollFailing)
        {
            return backlog is null
                ? Task.FromResult(HealthCheckResult.Unhealthy(
                    "Alert dispatcher is running but the backlog store is unreachable and no snapshot has been captured.",
                    data: data))
                : Task.FromResult(HealthCheckResult.Degraded(
                    "Alert dispatcher is running but the most recent backlog poll failed; the snapshot may be stale.",
                    data: data));
        }

        if (backlog is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Alert dispatcher initialized; awaiting first pass.",
                data));
        }

        if (backlog.PendingCount >= _options.Dispatch.DegradedBacklogThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Alert dispatch backlog of {backlog.PendingCount.ToString(CultureInfo.InvariantCulture)} rows exceeds threshold {_options.Dispatch.DegradedBacklogThreshold.ToString(CultureInfo.InvariantCulture)}.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Alert dispatcher is healthy.", data));
    }
}
