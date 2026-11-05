// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

internal readonly record struct PendingAlertDispatch(
    AlertEventEnvelope AlertEvent,
    ImmutableArray<AlertChannelType> Channels);

internal sealed partial class AlertDispatchWriter
{
    private readonly IAlertOutboxWriter _outboxWriter;
    private readonly AlertPipelineMetrics _metrics;
    private readonly ILogger<AlertDispatchWriter> _logger;

    public AlertDispatchWriter(
        IAlertOutboxWriter outboxWriter,
        AlertPipelineMetrics metrics,
        ILogger<AlertDispatchWriter> logger)
    {
        _outboxWriter = outboxWriter ?? throw new ArgumentNullException(nameof(outboxWriter));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PersistAsync(
        IReadOnlyCollection<AlertStateSnapshot> states,
        IReadOnlyList<PendingAlertDispatch> pendingDispatches,
        CancellationToken cancellationToken)
    {
        var entries = pendingDispatches
            .Select(static pending => new AlertOutboxEntry(pending.AlertEvent, pending.Channels))
            .ToArray();

        var inserted = await _outboxWriter.CommitEvaluationAsync(states, entries, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < pendingDispatches.Count; index++)
        {
            var pendingDispatch = pendingDispatches[index];
            if (!inserted[index])
            {
                LogEventDeduplicated(
                    _logger,
                    pendingDispatch.AlertEvent.DedupeKey,
                    pendingDispatch.AlertEvent.RuleId,
                    pendingDispatch.AlertEvent.ObjectId);
                continue;
            }

            _metrics.RecordEventEmitted(pendingDispatch.AlertEvent.TriggerType);

            foreach (var channel in pendingDispatch.Channels.Distinct())
            {
                _metrics.RecordDispatchEnqueued(channel);
            }
        }
    }

    [LoggerMessage(EventId = 9401, Level = LogLevel.Debug, Message = "Alert event deduplicated for key {DedupeKey} (rule {RuleId}, object {ObjectId}).")]
    private static partial void LogEventDeduplicated(ILogger logger, string dedupeKey, long ruleId, long objectId);
}
