// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Surfaces feature-change outbox dispatch state to readiness/health endpoints so
/// operators see backlog growth and dead-letter accumulation before they become silent
/// data loss. The dispatcher updates the backing snapshot after each pass.
/// </summary>
internal sealed class OutboxHealthCheck : IHealthCheck
{
    private readonly IOutboxCapabilityProvider _capability;
    private readonly IOutboxHealth _dispatcherHealth;
    private readonly OutboxDispatcherOptions _options;

    public OutboxHealthCheck(
        IOutboxCapabilityProvider capability,
        IOutboxHealth dispatcherHealth,
        IOptions<OutboxDispatcherOptions> options)
    {
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        _dispatcherHealth = dispatcherHealth ?? throw new ArgumentNullException(nameof(dispatcherHealth));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_capability.SupportsTransactionalOutbox)
        {
            // Non-capable providers fall back to the post-commit publish + retry queue path.
            // Health is reported as Healthy with the limitation surfaced in the description so
            // operators understand the durability tradeoff in the active deployment.
            var description = _capability.CapabilityLimitationDescription
                ?? "Provider does not support transactional outbox; using post-commit publish + retry queue.";
            return Task.FromResult(HealthCheckResult.Healthy(description));
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["dispatcher_running"] = _dispatcherHealth.IsDispatcherRunning,
        };

        if (_dispatcherHealth.LastDispatchAt is { } lastDispatchAt)
        {
            data["last_dispatch_at"] = lastDispatchAt.ToString("o", CultureInfo.InvariantCulture);
        }

        var backlog = _dispatcherHealth.LastBacklog;
        if (backlog is null)
        {
            // First pass has not run yet (or the dispatcher is disabled). Report Healthy with
            // a note so smoke checks immediately after startup do not flap on cold state.
            return Task.FromResult(HealthCheckResult.Healthy(
                "Outbox dispatcher initialized; awaiting first pass.",
                data));
        }

        data["pending_count"] = backlog.PendingCount;
        data["dead_lettered_count"] = backlog.DeadLetteredCount;
        data["oldest_pending_age_seconds"] = backlog.OldestPendingAgeSeconds;

        if (backlog.DeadLetteredCount >= _options.UnhealthyDeadLetterThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Outbox has {backlog.DeadLetteredCount.ToString(CultureInfo.InvariantCulture)} dead-lettered rows requiring operator triage.",
                data: data));
        }

        if (!_dispatcherHealth.IsDispatcherRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Outbox dispatcher is not running; pending events will accumulate until it restarts.",
                data: data));
        }

        if (backlog.PendingCount >= _options.DegradedBacklogThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Outbox backlog of {backlog.PendingCount.ToString(CultureInfo.InvariantCulture)} rows exceeds threshold {_options.DegradedBacklogThreshold.ToString(CultureInfo.InvariantCulture)}.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Outbox dispatcher is healthy.", data));
    }
}
