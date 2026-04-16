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
            // create duplicate runs for the same cron firing. Move this replica's cursor past
            // the occurrence regardless of who won the claim.
            var claimed = await definitionStore.TryClaimScheduleFireAsync(
                definition.WorkflowId,
                next.Value,
                ScheduleClaimRetention,
                cancellationToken).ConfigureAwait(false);

            _compiled[definition.WorkflowId] = cached with { LastFireAt = next };

            // Persist the cursor immediately so the next startup (even beyond the claim TTL
            // window) resumes after this occurrence. Advancing is monotonic so a competing
            // replica that already advanced past this point keeps its newer cursor.
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
                // Best-effort — a durable cursor write failure does not block the active tick.
                // The next successful tick will try again; in the meantime the in-memory cursor
                // and schedule-claim TTL still protect against duplicate firings.
                OrchestrationLog.SchedulerTickFailed(logger, ex);
            }

            if (!claimed)
            {
                continue;
            }

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
            }
            catch (WorkflowDefinitionValidationException ex)
            {
                OrchestrationLog.SchedulerTickFailed(logger, ex);
            }
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
