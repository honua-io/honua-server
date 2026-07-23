// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Adapts the cron workflow-scheduler tick to the control-plane scheduled-tick dispatcher so
/// EventBridge Scheduler can drive one evaluation under <c>TriggerMode=Event</c> without hosting the
/// in-process timer. Wraps the SAME singleton <see cref="WorkflowSchedulerBackgroundService" /> the
/// timer would host, so the in-memory compiled-cron cache survives across ticks. The tick is
/// idempotent via a durable per-occurrence claim plus a durable schedule cursor.
/// </summary>
internal sealed class WorkflowSchedulerScheduledTickHandler(WorkflowSchedulerBackgroundService service)
    : Honua.Core.Features.ControlPlane.Abstractions.IScheduledTickHandler
{
    public Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind Kind
        => Honua.Core.Features.ControlPlane.Abstractions.ScheduledTickKind.WorkflowSchedule;

    public Task RunTickAsync(CancellationToken cancellationToken = default)
        => service.TickAsync(cancellationToken);
}

/// <summary>
/// Background worker that evaluates cron-triggered workflow definitions and creates
/// workflow runs when their schedule fires. Runs one tick per minute, producing at most
/// one run per definition per tick.
/// </summary>
internal sealed class WorkflowSchedulerBackgroundService(
    IWorkflowDefinitionStore definitionStore,
    WorkflowOrchestrationEngine engine,
    TimeProvider clock,
    ILogger<WorkflowSchedulerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScheduleClaimRetention = TimeSpan.FromHours(24);

    // Compiled cron expressions, keyed by workflow id. Recomputed lazily when the
    // definition's cron expression changes.
    private readonly ConcurrentDictionary<string, CachedCron> _compiled = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OrchestrationLog.SchedulerBackgroundServiceStarted(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            // Intentionally generic: this is a long-running background loop; a single failed
            // tick must not kill the host — log and keep polling.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OrchestrationLog.SchedulerTickFailed(logger, ex);
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        using var activity = Honua.ServiceDefaults.HonuaTelemetry.ActivitySource.StartActivity(
            OrchestrationTelemetry.Activities.SchedulerTick,
            System.Diagnostics.ActivityKind.Internal);

        var now = clock.GetUtcNow();
        var scheduled = await definitionStore.ListScheduledAsync(cancellationToken).ConfigureAwait(false);

        // Evict compiled-cron cache entries for workflow ids that no longer appear in the
        // scheduled list. Without this, a definition that is deleted and recreated with the
        // same id would inherit its predecessor's LastFireAt and skip early occurrences.
        EvictCompiledCacheForMissingWorkflows(scheduled);

        foreach (var definition in scheduled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.Trigger is null || string.IsNullOrWhiteSpace(definition.Trigger.CronExpression))
            {
                continue;
            }

            var cached = _compiled.GetOrAdd(
                definition.WorkflowId,
                _ => CompileOrNull(definition));

            if (cached.Expression != definition.Trigger.CronExpression ||
                !string.Equals(cached.TimeZoneId, definition.Trigger.TimeZone ?? TimeZoneInfo.Utc.Id, StringComparison.Ordinal))
            {
                cached = CompileOrNull(definition);
                _compiled[definition.WorkflowId] = cached;
            }

            if (cached.Cron is null || cached.TimeZone is null)
            {
                if (!cached.InvalidLogged)
                {
                    OrchestrationLog.SchedulerDefinitionInvalid(
                        logger,
                        definition.WorkflowId,
                        cached.Expression,
                        cached.TimeZoneId);
                    _compiled[definition.WorkflowId] = cached with { InvalidLogged = true };
                }

                continue;
            }

            // Seed the in-memory cursor from the durable cursor on first encounter after a
            // restart so we never rewind into previously-fired occurrences. The per-firing
            // claim key only survives ScheduleClaimRetention; the cursor is the long-lived
            // source of truth for replay protection.
            if (cached.LastFireAt is null)
            {
                var durableCursor = await definitionStore
                    .GetScheduleCursorAsync(definition.WorkflowId, cancellationToken)
                    .ConfigureAwait(false);
                var seeded = durableCursor ?? definition.UpdatedAt;
                cached = cached with { LastFireAt = seeded };
                _compiled[definition.WorkflowId] = cached;
            }

            if (cached.LastFireAt < definition.UpdatedAt)
            {
                cached = cached with { LastFireAt = definition.UpdatedAt };
                _compiled[definition.WorkflowId] = cached;
            }

            if (cached.PendingCursorAt is { } pendingCursor)
            {
                try
                {
                    await definitionStore
                        .AdvanceScheduleCursorAsync(definition.WorkflowId, pendingCursor, cancellationToken)
                        .ConfigureAwait(false);
                    cached = cached with { PendingCursorAt = null };
                    _compiled[definition.WorkflowId] = cached;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Intentional catch-all: advancing the durable cursor here is best-effort;
                // leaving PendingCursorAt set means the next tick will simply retry the advance.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    OrchestrationLog.SchedulerTickFailed(logger, ex);
                }
            }

            var lastFire = cached.LastFireAt ?? definition.UpdatedAt;
            var next = cached.Cron!.GetNextOccurrence(lastFire, cached.TimeZone!);
            if (next is null || next.Value > now)
            {
                continue;
            }

            // Atomically claim the fire-time occurrence so replicas and restarts can never
            // create duplicate runs for the same cron firing.
            var claimed = await definitionStore.TryClaimScheduleFireAsync(
                definition.WorkflowId,
                next.Value,
                ScheduleClaimRetention,
                cancellationToken).ConfigureAwait(false);

            if (!claimed)
            {
                var durableCursor = await definitionStore
                    .GetScheduleCursorAsync(definition.WorkflowId, cancellationToken)
                    .ConfigureAwait(false);
                if (durableCursor.HasValue && durableCursor.Value >= next.Value)
                {
                    _compiled[definition.WorkflowId] = cached with { LastFireAt = durableCursor.Value };
                }

                continue;
            }

            // Winner path: create the run first so the durable cursor only moves past the
            // occurrence when the firing has actually been materialised (or the definition
            // is permanently invalid). A transient CreateRunAsync failure must release the
            // claim and leave the cursor untouched so the next tick can retry the same
            // occurrence — otherwise a one-off store outage would silently drop the run.
            var runCreated = false;
            var permanentFailure = false;
            var durablePendingWritten = false;
            try
            {
                var principal = OrchestrationSystemPrincipal.Create(null);
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["scheduler.fire_time"] = next.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                };

                await engine.CreateRunAsync(
                    definition,
                    WorkflowTriggerKind.Cron,
                    principal,
                    metadata,
                    cancellationToken).ConfigureAwait(false);

                OrchestrationLog.SchedulerTriggered(logger, definition.WorkflowId, next.Value);
                runCreated = true;

                try
                {
                    await definitionStore
                        .SetPendingScheduleCursorAsync(definition.WorkflowId, next.Value, cancellationToken)
                        .ConfigureAwait(false);
                    durablePendingWritten = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Intentional catch-all: the pending-cursor write is best-effort here; a failure
                // just means the cursor advance is retried (or reconciled) on a later tick.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    OrchestrationLog.SchedulerTickFailed(logger, ex);
                }
            }
            catch (WorkflowDefinitionValidationException ex)
            {
                OrchestrationLog.SchedulerTickFailed(logger, ex);
                permanentFailure = true;

                try
                {
                    await definitionStore
                        .SetPendingScheduleCursorAsync(definition.WorkflowId, next.Value, cancellationToken)
                        .ConfigureAwait(false);
                    durablePendingWritten = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Intentional catch-all: same best-effort pending-cursor write as the success
                // path above; a failure here is retried on a later tick.
                catch (Exception pendingEx) when (pendingEx is not OutOfMemoryException)
                {
                    OrchestrationLog.SchedulerTickFailed(logger, pendingEx);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryReleaseScheduleClaimAsync(definition.WorkflowId, next.Value).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Transient: release the claim so another tick (on this replica or a peer)
                // can retry the occurrence. Do not advance cursors; the retry depends on
                // them still pointing at this firing.
                OrchestrationLog.SchedulerTickFailed(logger, ex);
                await TryReleaseScheduleClaimAsync(definition.WorkflowId, next.Value).ConfigureAwait(false);
                continue;
            }

            if (!runCreated && !permanentFailure)
            {
                continue;
            }

            // Only advance the in-memory cursor when at least one durable write
            // (pending cursor or cursor advance) has succeeded. Without durable
            // protection a crash would lose the in-memory LastFireAt, allowing the
            // occurrence to replay after the claim TTL expires.
            var advanced = cached with
            {
                LastFireAt = durablePendingWritten ? next : cached.LastFireAt,
                PendingCursorAt = next
            };
            _compiled[definition.WorkflowId] = advanced;

            try
            {
                await definitionStore
                    .AdvanceScheduleCursorAsync(definition.WorkflowId, next.Value, cancellationToken)
                    .ConfigureAwait(false);
                _compiled[definition.WorkflowId] = advanced with { PendingCursorAt = null, LastFireAt = next };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: advancing the durable cursor here is best-effort; the
            // in-memory cursor already moved above, and a failed durable advance is retried
            // (or reconciled from PendingCursorAt) on a later tick.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OrchestrationLog.SchedulerTickFailed(logger, ex);
            }
        }
    }

    private void EvictCompiledCacheForMissingWorkflows(IReadOnlyList<WorkflowDefinition> scheduled)
    {
        if (_compiled.IsEmpty)
        {
            return;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in scheduled)
        {
            present.Add(definition.WorkflowId);
        }

        foreach (var cachedId in _compiled.Keys.Where(cachedId => !present.Contains(cachedId)))
        {
            _compiled.TryRemove(cachedId, out _);
        }
    }

    private async Task TryReleaseScheduleClaimAsync(string workflowId, DateTimeOffset fireTime)
    {
        try
        {
            await definitionStore
                .ReleaseScheduleClaimAsync(workflowId, fireTime, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // If release fails, the claim TTL still bounds the worst case: the slot stays
            // reserved until the retention expires. Log but do not surface the failure.
            OrchestrationLog.SchedulerTickFailed(logger, ex);
        }
    }

    private static CachedCron CompileOrNull(WorkflowDefinition definition)
    {
        // Callers only invoke this after confirming Trigger/CronExpression are present, but guard
        // explicitly here too so the method is correct on its own rather than relying on a blind
        // null-forgiving operator against an external invariant.
        if (definition.Trigger is null || string.IsNullOrWhiteSpace(definition.Trigger.CronExpression))
        {
            return new CachedCron(
                definition.Trigger?.CronExpression ?? string.Empty,
                definition.Trigger?.TimeZone ?? TimeZoneInfo.Utc.Id,
                null,
                null,
                null,
                false,
                null);
        }

        // Trigger and its CronExpression are guaranteed non-null/non-whitespace past the guard
        // above, so no null-conditional/coalescing is needed for them below.
        try
        {
            var expression = definition.Trigger.CronExpression;
            var timeZoneId = definition.Trigger.TimeZone ?? TimeZoneInfo.Utc.Id;
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var cron = CronExpression.Parse(expression);
            return new CachedCron(expression, timeZoneId, cron, timeZone, null, false, null);
        }
        // Intentional catch-all: TimeZoneInfo.FindSystemTimeZoneById and CronExpression.Parse
        // can throw several distinct exception types (invalid id, invalid expression, platform
        // ICU/timezone-db lookup failures); any of them means the schedule is unusable and
        // must fall back to the invalid-cron sentinel below rather than crash the tick loop.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Not logged here: this is a static helper with no logger access. The caller
            // (TickAsync) detects the resulting Cron/TimeZone == null and logs it exactly
            // once per definition via OrchestrationLog.SchedulerDefinitionInvalid, tracked
            // by CachedCron.InvalidLogged so a persistently-invalid cron expression doesn't
            // spam the log on every tick.
            return new CachedCron(
                definition.Trigger.CronExpression,
                definition.Trigger.TimeZone ?? TimeZoneInfo.Utc.Id,
                null,
                null,
                null,
                false,
                null);
        }
    }

    private sealed record CachedCron(
        string Expression,
        string TimeZoneId,
        CronExpression? Cron,
        TimeZoneInfo? TimeZone,
        DateTimeOffset? LastFireAt,
        bool InvalidLogged,
        DateTimeOffset? PendingCursorAt);
}
