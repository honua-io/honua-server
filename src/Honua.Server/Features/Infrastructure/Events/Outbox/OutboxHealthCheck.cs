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

        // Check dispatcher liveness before any "still warming up" branch. A dispatcher
        // that exited before its first pass leaves LastBacklog=null but is no longer
        // claiming rows, so pending events would accumulate silently if we treated the
        // null-backlog state as Healthy. The runbook contract is "Unhealthy when the
        // dispatcher is not running"; this guard honors that even on cold start.
        if (!_dispatcherHealth.IsDispatcherRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Outbox dispatcher is not running; pending events will accumulate until it restarts.",
                data: data));
        }

        AddPollTimestampData(data, "claim", _dispatcherHealth.LastClaimPollSuccessAt, _dispatcherHealth.LastClaimPollFailureAt);
        AddPollTimestampData(data, "recovery", _dispatcherHealth.LastRecoveryPollSuccessAt, _dispatcherHealth.LastRecoveryPollFailureAt);
        AddPollTimestampData(data, "backlog", _dispatcherHealth.LastBacklogPollSuccessAt, _dispatcherHealth.LastBacklogPollFailureAt);
        if (_dispatcherHealth.LastStorageFailureAt is { } lastStorageFailureAt)
        {
            data["last_storage_failure_at"] = lastStorageFailureAt.ToString("o", CultureInfo.InvariantCulture);
        }
        if (_dispatcherHealth.LastSuccessfulPollAt is { } lastSuccessfulPollAt)
        {
            data["last_successful_poll_at"] = lastSuccessfulPollAt.ToString("o", CultureInfo.InvariantCulture);
        }

        var backlog = _dispatcherHealth.LastBacklog;
        if (backlog is not null)
        {
            data["pending_count"] = backlog.PendingCount;
            data["dead_lettered_count"] = backlog.DeadLetteredCount;
            data["oldest_pending_age_seconds"] = backlog.OldestPendingAgeSeconds;

            // Dead-lettered rows demand operator triage regardless of whether the
            // most recent storage poll just failed. Evaluating this before the
            // storage-poll branch prevents a stale-but-known backlog with dead
            // letters from being demoted to Degraded by an intermittent claim or
            // backlog query failure.
            if (backlog.DeadLetteredCount >= _options.UnhealthyDeadLetterThreshold)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Outbox has {backlog.DeadLetteredCount.ToString(CultureInfo.InvariantCulture)} dead-lettered rows requiring operator triage.",
                    data: data));
            }
        }

        // Storage-poll failures are tracked per kind (claim, recovery, backlog)
        // so a successful backlog read does not mask a still-failing claim or a
        // still-failing recovery sweep. IsStoragePollFailing returns true when any
        // kind has a failure timestamp newer than its own most recent success.
        if (_dispatcherHealth.IsStoragePollFailing)
        {
            if (backlog is null)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Outbox dispatcher is running but storage polling is failing and no backlog snapshot has been captured.",
                    data: data));
            }

            return Task.FromResult(HealthCheckResult.Degraded(
                "Outbox dispatcher is running but the most recent storage poll failed; backlog snapshot may be stale.",
                data: data));
        }

        if (backlog is null)
        {
            // Dispatcher is running but the first pass has not produced a backlog snapshot
            // yet. Report Healthy with a note so smoke checks immediately after startup do
            // not flap on cold state.
            return Task.FromResult(HealthCheckResult.Healthy(
                "Outbox dispatcher initialized; awaiting first pass.",
                data));
        }

        if (backlog.PendingCount >= _options.DegradedBacklogThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Outbox backlog of {backlog.PendingCount.ToString(CultureInfo.InvariantCulture)} rows exceeds threshold {_options.DegradedBacklogThreshold.ToString(CultureInfo.InvariantCulture)}.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Outbox dispatcher is healthy.", data));
    }

    private static void AddPollTimestampData(
        Dictionary<string, object> data,
        string kind,
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset? lastFailureAt)
    {
        if (lastSuccessAt is { } success)
        {
            data[$"last_{kind}_poll_success_at"] = success.ToString("o", CultureInfo.InvariantCulture);
        }
        if (lastFailureAt is { } failure)
        {
            data[$"last_{kind}_poll_failure_at"] = failure.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
