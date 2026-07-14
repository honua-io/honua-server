// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Background worker that evaluates event/CDC-triggered workflow definitions and creates runs
/// when a watched source advances:
/// <list type="bullet">
///   <item>change-feed (<see cref="WorkflowTriggerKind.ChangeFeed"/>): a watched layer's
///   <c>feature_changes</c> generation advances past the durable change-feed cursor;</item>
///   <item>object-store (<see cref="WorkflowTriggerKind.ObjectStore"/>): a new object lands under
///   a watched prefix, advancing the object marker past the durable object-store cursor.</item>
/// </list>
/// HA safety mirrors the cron scheduler: the firing marker is atomically claimed via the durable
/// fire-claim before a run is created, so replicas and restarts never double-fire a given marker.
/// </summary>
internal sealed class WorkflowEventTriggerBackgroundService(
    IWorkflowDefinitionStore definitionStore,
    WorkflowOrchestrationEngine engine,
    IChangeFeedGenerationProbe changeFeedProbe,
    IObjectStoreTriggerProbe objectStoreProbe,
    TimeProvider clock,
    ILogger<WorkflowEventTriggerBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TriggerClaimRetention = TimeSpan.FromHours(24);

    // Tracks the last poll time per object-store workflow so a configured PollInterval can throttle
    // store probes without throttling change-feed evaluation. In-memory only; a missed poll simply
    // fires on the next tick.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastObjectStorePoll = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OrchestrationLog.EventTriggerBackgroundServiceStarted(logger);

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
            // Intentionally generic: this is a long-running background polling loop. A
            // single failed tick (e.g. a transient store/probe failure) must not kill the
            // host's background service; log and keep polling on the next tick.
            catch (Exception ex)
            {
                OrchestrationLog.EventTriggerTickFailed(logger, ex);
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
            OrchestrationTelemetry.Activities.EventTriggerTick,
            System.Diagnostics.ActivityKind.Internal);

        var definitions = await definitionStore.ListEventTriggeredAsync(cancellationToken).ConfigureAwait(false);

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trigger = definition.Trigger;
            if (trigger is null || !trigger.Enabled)
            {
                continue;
            }

            try
            {
                switch (trigger.Kind)
                {
                    case WorkflowTriggerKind.ChangeFeed:
                        await EvaluateChangeFeedAsync(definition, trigger, cancellationToken).ConfigureAwait(false);
                        break;
                    case WorkflowTriggerKind.ObjectStore:
                        await EvaluateObjectStoreAsync(definition, trigger, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        // Cron/Manual are handled elsewhere; ignore.
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: per-definition loop over event-triggered workflow
            // definitions. One definition's trigger evaluation failing must not prevent the
            // remaining definitions in this tick from being evaluated.
            catch (Exception ex)
            {
                OrchestrationLog.EventTriggerTickFailed(logger, ex);
            }
        }
    }

    private async Task EvaluateChangeFeedAsync(
        WorkflowDefinition definition,
        WorkflowTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (trigger.WatchedLayerIds.Count == 0)
        {
            return;
        }

        var cursorMarker = await definitionStore
            .GetTriggerCursorAsync(definition.WorkflowId, RedisWorkflowDefinitionStore.ChangeFeedCursorKind, cancellationToken)
            .ConfigureAwait(false);
        var sinceGeneration = ParseGeneration(cursorMarker);

        var latest = await changeFeedProbe
            .GetLatestGenerationAsync(sinceGeneration, trigger.WatchedLayerIds, cancellationToken)
            .ConfigureAwait(false);

        if (latest is not { } generation || generation <= sinceGeneration)
        {
            // No watched layer advanced past the cursor — do not fire.
            return;
        }

        var marker = EncodeGeneration(generation);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trigger.kind"] = nameof(WorkflowTriggerKind.ChangeFeed),
            ["trigger.change_feed.generation"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["trigger.change_feed.since_generation"] = sinceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["trigger.change_feed.watched_layers"] = string.Join(',', trigger.WatchedLayerIds)
        };

        var fired = await FireAsync(
            definition,
            RedisWorkflowDefinitionStore.ChangeFeedCursorKind,
            marker,
            metadata,
            cancellationToken).ConfigureAwait(false);

        if (fired)
        {
            OrchestrationLog.ChangeFeedTriggerFired(logger, definition.WorkflowId, generation);
        }
    }

    private async Task EvaluateObjectStoreAsync(
        WorkflowDefinition definition,
        WorkflowTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (trigger.ObjectStore is not { } config || string.IsNullOrWhiteSpace(config.StoreId))
        {
            return;
        }

        // Honour the per-trigger poll interval so high-frequency ticks do not hammer the store.
        if (config.PollInterval is { } interval && interval > TimeSpan.Zero)
        {
            var now = clock.GetUtcNow();
            if (_lastObjectStorePoll.TryGetValue(definition.WorkflowId, out var lastPoll)
                && now - lastPoll < interval)
            {
                return;
            }

            _lastObjectStorePoll[definition.WorkflowId] = now;
        }

        var newest = await objectStoreProbe.ProbeNewestAsync(config, cancellationToken).ConfigureAwait(false);
        if (newest is not { } result)
        {
            // Empty store/prefix — nothing to fire.
            return;
        }

        var cursorMarker = await definitionStore
            .GetTriggerCursorAsync(definition.WorkflowId, RedisWorkflowDefinitionStore.ObjectStoreCursorKind, cancellationToken)
            .ConfigureAwait(false);

        // First observation seeds the cursor without firing so an already-present backlog of objects
        // does not stampede a run on first deployment. Only objects that land after the seed fire.
        if (cursorMarker is null)
        {
            await definitionStore
                .SetTriggerCursorAsync(definition.WorkflowId, RedisWorkflowDefinitionStore.ObjectStoreCursorKind, result.Marker, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.CompareOrdinal(result.Marker, cursorMarker) <= 0)
        {
            // No new object landed past the cursor — do not fire.
            return;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trigger.kind"] = nameof(WorkflowTriggerKind.ObjectStore),
            ["trigger.object_store.store_id"] = config.StoreId,
            ["trigger.object_store.prefix"] = config.Prefix,
            ["trigger.object_store.key"] = result.Key,
            ["trigger.object_store.last_modified"] = result.LastModified.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };

        var fired = await FireAsync(
            definition,
            RedisWorkflowDefinitionStore.ObjectStoreCursorKind,
            result.Marker,
            metadata,
            cancellationToken).ConfigureAwait(false);

        if (fired)
        {
            OrchestrationLog.ObjectStoreTriggerFired(logger, definition.WorkflowId, result.Key);
        }
    }

    /// <summary>
    /// Atomically claims the firing marker, creates the run, and advances the durable cursor.
    /// Returns true only when this caller created the run. A lost claim (another replica won) or a
    /// not-advanced marker yields false. Transient run-creation failures release the claim and leave
    /// the cursor untouched so the next tick retries the same marker.
    /// </summary>
    private async Task<bool> FireAsync(
        WorkflowDefinition definition,
        string cursorKind,
        string marker,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var claimed = await definitionStore
            .TryClaimTriggerFireAsync(definition.WorkflowId, cursorKind, marker, TriggerClaimRetention, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            // Another replica already claimed this marker. Only advance our cursor once that
            // replica has durably committed the firing (its cursor reflects the marker). If the
            // winner is still mid-flight — or crashed and released the claim before committing —
            // we leave our cursor where it is so the marker can be re-claimed and retried by a
            // future tick, preserving at-least-one-eventually semantics on a persistent advance.
            var durableCursor = await definitionStore
                .GetTriggerCursorAsync(definition.WorkflowId, cursorKind, cancellationToken)
                .ConfigureAwait(false);
            if (durableCursor is not null && string.CompareOrdinal(durableCursor, marker) >= 0)
            {
                await definitionStore
                    .SetTriggerCursorAsync(definition.WorkflowId, cursorKind, marker, cancellationToken)
                    .ConfigureAwait(false);
            }

            return false;
        }

        try
        {
            var principal = OrchestrationSystemPrincipal.Create(null);
            var triggerKind = cursorKind == RedisWorkflowDefinitionStore.ChangeFeedCursorKind
                ? WorkflowTriggerKind.ChangeFeed
                : WorkflowTriggerKind.ObjectStore;

            await engine
                .CreateRunAsync(definition, triggerKind, principal, metadata, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowDefinitionValidationException ex)
        {
            // Permanent failure: the definition can never fire. Advance the cursor so we stop
            // retrying a marker that will always fail validation.
            OrchestrationLog.EventTriggerTickFailed(logger, ex);
            await definitionStore
                .SetTriggerCursorAsync(definition.WorkflowId, cursorKind, marker, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryReleaseClaimAsync(definition.WorkflowId, cursorKind, marker).ConfigureAwait(false);
            throw;
        }
        // Intentional catch-all: run creation can fail for many transient reasons (engine,
        // storage, dependency failures); release the claim and leave the cursor so the next
        // tick retries the same marker instead of losing the fire attempt.
        catch (Exception ex)
        {
            OrchestrationLog.EventTriggerTickFailed(logger, ex);
            await TryReleaseClaimAsync(definition.WorkflowId, cursorKind, marker).ConfigureAwait(false);
            return false;
        }

        // Run created: advance the durable cursor past the fired marker so it is never replayed.
        await definitionStore
            .SetTriggerCursorAsync(definition.WorkflowId, cursorKind, marker, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task TryReleaseClaimAsync(string workflowId, string cursorKind, string marker)
    {
        try
        {
            await definitionStore
                .ReleaseTriggerClaimAsync(workflowId, cursorKind, marker, CancellationToken.None)
                .ConfigureAwait(false);
        }
        // Intentional catch-all: best-effort claim release after a failed or cancelled fire
        // attempt; a failure here must not propagate, the claim simply expires via its own
        // retention window instead.
        catch (Exception ex)
        {
            OrchestrationLog.EventTriggerTickFailed(logger, ex);
        }
    }

    private static long ParseGeneration(string? marker)
        => long.TryParse(marker, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0L;

    // Zero-padded so the change-feed generation cursor compares correctly with the ordinal
    // string compare used by the shared trigger-cursor mechanism.
    private static string EncodeGeneration(long generation)
        => generation.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);
}
