// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

/// <summary>
/// Represents an alert event paired with the channels it should be delivered to once persisted.
/// </summary>
/// <param name="AlertEvent">The alert event envelope awaiting append and dispatch.</param>
/// <param name="Channels">The deliverable channels resolved for the originating rule.</param>
internal readonly record struct PendingAlertDispatch(
    AlertEventEnvelope AlertEvent,
    ImmutableArray<AlertChannelType> Channels);

/// <summary>
/// Persists evaluated alert events and enqueues their channel dispatches, deduplicating events that
/// have already been appended. Extracted from <see cref="AlertPipeline"/> so the pipeline composes a
/// single dispatch-persistence collaborator instead of injecting the event and dispatch stores directly.
/// </summary>
internal sealed partial class AlertDispatchWriter
{
    private readonly IAlertEventStore _eventStore;
    private readonly IAlertDispatchStore _dispatchStore;
    private readonly ILogger<AlertDispatchWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertDispatchWriter"/> class.
    /// </summary>
    /// <param name="eventStore">Store used to append alert events with deduplication semantics.</param>
    /// <param name="dispatchStore">Store used to enqueue channel dispatches for appended events.</param>
    /// <param name="logger">Logger for dispatch-persistence diagnostics.</param>
    public AlertDispatchWriter(
        IAlertEventStore eventStore,
        IAlertDispatchStore dispatchStore,
        ILogger<AlertDispatchWriter> logger)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _dispatchStore = dispatchStore ?? throw new ArgumentNullException(nameof(dispatchStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Appends each pending alert event and enqueues its channel dispatch. Events that the event store
    /// reports as duplicates are logged and skipped without enqueuing a dispatch.
    /// </summary>
    /// <param name="pendingDispatches">The pending dispatches to persist, in evaluation order.</param>
    /// <param name="cancellationToken">A token to observe while awaiting the persistence operations.</param>
    public async Task PersistAsync(
        IReadOnlyList<PendingAlertDispatch> pendingDispatches,
        CancellationToken cancellationToken)
    {
        foreach (var pendingDispatch in pendingDispatches)
        {
            var eventId = await _eventStore.TryAppendAsync(pendingDispatch.AlertEvent, cancellationToken).ConfigureAwait(false);
            if (!eventId.HasValue)
            {
                LogEventDeduplicated(
                    _logger,
                    pendingDispatch.AlertEvent.DedupeKey,
                    pendingDispatch.AlertEvent.RuleId,
                    pendingDispatch.AlertEvent.ObjectId);
                continue;
            }

            AlertPipelineMetrics.RecordEventEmitted(pendingDispatch.AlertEvent.TriggerType);

            await _dispatchStore.EnqueueAsync(eventId.Value, pendingDispatch.Channels, cancellationToken).ConfigureAwait(false);

            foreach (var channel in pendingDispatch.Channels.Distinct())
            {
                AlertPipelineMetrics.RecordDispatchEnqueued(channel);
            }
        }
    }

    [LoggerMessage(EventId = 9401, Level = LogLevel.Debug, Message = "Alert event deduplicated for key {DedupeKey} (rule {RuleId}, object {ObjectId}).")]
    private static partial void LogEventDeduplicated(ILogger logger, string dedupeKey, long ruleId, long objectId);
}
