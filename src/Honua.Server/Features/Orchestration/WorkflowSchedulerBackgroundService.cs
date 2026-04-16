// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Orchestration;

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
            catch (Exception ex)
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

            var lastFire = cached.LastFireAt ?? definition.UpdatedAt;
            var next = cached.Cron.GetNextOccurrence(lastFire, cached.TimeZone);
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
                // Another replica owns this occurrence. Advance only this replica's
                // in-memory cursor so we don't spin on the claim key, and leave the
                // durable cursor for the winning replica to advance after it persists
                // the run. Losers never advance the durable cursor: that would consume
                // the occurrence even if the winner ultimately hit a transient failure
                // and needs to retry.
                _compiled[definition.WorkflowId] = cached with { LastFireAt = next };
                continue;
            }

            // Winner path: create the run first so the durable cursor only moves past the
            // occurrence when the firing has actually been materialised (or the definition
            // is permanently invalid). A transient CreateRunAsync failure must release the
            // claim and leave the cursor untouched so the next tick can retry the same
            // occurrence — otherwise a one-off store outage would silently drop the run.
            var runCreated = false;
            var permanentFailure = false;
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
            }
            catch (WorkflowDefinitionValidationException ex)
            {
                // Permanent: the definition is structurally invalid. Advance past the
                // occurrence so the next tick moves on instead of re-attempting the same
                // broken firing every interval.
                OrchestrationLog.SchedulerTickFailed(logger, ex);
                permanentFailure = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryReleaseScheduleClaimAsync(definition.WorkflowId, next.Value).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
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

            _compiled[definition.WorkflowId] = cached with { LastFireAt = next };

            // Persist the cursor so the next startup (even beyond the claim TTL window)
            // resumes after this occurrence. AdvanceScheduleCursorAsync is monotonic, so
            // a competing replica that already advanced past this point keeps its newer
            // cursor. Failure to persist is best-effort: the in-memory cursor still
            // protects this replica; the next successful tick will retry the write.
            try
            {
                await definitionStore
                    .AdvanceScheduleCursorAsync(definition.WorkflowId, next.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
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

        foreach (var cachedId in _compiled.Keys)
        {
            if (!present.Contains(cachedId))
            {
                _compiled.TryRemove(cachedId, out _);
            }
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
        catch (Exception ex)
        {
            // If release fails, the claim TTL still bounds the worst case: the slot stays
            // reserved until the retention expires. Log but do not surface the failure.
            OrchestrationLog.SchedulerTickFailed(logger, ex);
        }
    }

    private static CachedCron CompileOrNull(WorkflowDefinition definition)
    {
        try
        {
            var expression = definition.Trigger!.CronExpression!;
            var timeZoneId = definition.Trigger.TimeZone ?? TimeZoneInfo.Utc.Id;
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var cron = CronExpression.Parse(expression);
            return new CachedCron(expression, timeZoneId, cron, timeZone, null, false);
        }
        catch (Exception)
        {
            return new CachedCron(
                definition.Trigger?.CronExpression ?? string.Empty,
                definition.Trigger?.TimeZone ?? TimeZoneInfo.Utc.Id,
                null,
                null,
                null,
                false);
        }
    }

    private sealed record CachedCron(
        string Expression,
        string TimeZoneId,
        CronExpression? Cron,
        TimeZoneInfo? TimeZone,
        DateTimeOffset? LastFireAt,
        bool InvalidLogged);
}
