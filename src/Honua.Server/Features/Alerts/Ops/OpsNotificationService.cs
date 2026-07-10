// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts.Ops;

/// <summary>
/// Composes operations notifications (deploy/job terminal events) into ops alert
/// events and enqueues them through the SAME delivery outbox the geofence pipeline
/// uses (<see cref="AlertDispatchWriter"/> -&gt; <c>alert_dispatch</c> -&gt;
/// <c>AlertDispatchBackgroundService</c> -&gt; <see cref="IAlertDeliverySink"/>). This
/// is the second consumer that proves the outbox layer is consumer-agnostic: ops
/// events carry <see cref="AlertEventSources.Ops"/> and a NULL rule link, and reuse
/// the identical channel sinks, retry, dedupe, and rate-cap machinery.
/// </summary>
internal sealed partial class OpsNotificationService
{
    private readonly AlertDispatchWriter _dispatchWriter;
    private readonly IAlertEditionPolicy _editionPolicy;
    private readonly AlertChannelCircuitBreaker _circuitBreaker;
    private readonly AlertOptions _options;
    private readonly ILogger<OpsNotificationService> _logger;

    public OpsNotificationService(
        AlertDispatchWriter dispatchWriter,
        IAlertEditionPolicy editionPolicy,
        AlertChannelCircuitBreaker circuitBreaker,
        IOptions<AlertOptions> options,
        ILogger<OpsNotificationService> logger)
    {
        _dispatchWriter = dispatchWriter ?? throw new ArgumentNullException(nameof(dispatchWriter));
        _editionPolicy = editionPolicy ?? throw new ArgumentNullException(nameof(editionPolicy));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Composes and enqueues one ops notification. No-ops when ops notifications are
    /// disabled or the severity is below the configured minimum. When no configured
    /// channel is currently deliverable, the event is still persisted without a
    /// dispatch row so operator evidence survives channel circuit breaking and
    /// edition gating. Idempotent per terminal event via the outbox dedupe key.
    /// </summary>
    public async Task NotifyAsync(OpsNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var ops = _options.Ops;
        if (!ops.Enabled)
        {
            AlertPipelineMetrics.RecordOpsNotification(notification.Source, notification.Severity, "disabled");
            return;
        }

        if ((int)notification.Severity < (int)ops.MinSeverity)
        {
            AlertPipelineMetrics.RecordOpsNotification(notification.Source, notification.Severity, "below_min_severity");
            LogBelowMinSeverity(_logger, notification.Source, notification.Severity, ops.MinSeverity);
            return;
        }

        var channels = ResolveChannels(ops.Channels, notification.Source, DateTimeOffset.UtcNow);
        var envelope = BuildEnvelope(notification);
        await _dispatchWriter
            .PersistAsync([new PendingAlertDispatch(envelope, channels)], cancellationToken)
            .ConfigureAwait(false);

        if (channels.IsDefaultOrEmpty)
        {
            AlertPipelineMetrics.RecordOpsNotification(notification.Source, notification.Severity, "persisted_no_channel");
            LogPersistedWithoutChannel(_logger, notification.Source, notification.Severity, notification.DedupeIdentifier);
        }
        else
        {
            AlertPipelineMetrics.RecordOpsNotification(notification.Source, notification.Severity, "enqueued");
            LogEnqueued(_logger, notification.Source, notification.Severity, channels.Length, notification.DedupeIdentifier);
        }
    }

    private ImmutableArray<AlertChannelType> ResolveChannels(IReadOnlyList<string> configured, string source, DateTimeOffset now)
    {
        var builder = ImmutableArray.CreateBuilder<AlertChannelType>();
        var seen = new HashSet<AlertChannelType>();

        foreach (var name in configured)
        {
            if (!AlertChannelNames.TryParse(name, out var channelType) || !seen.Add(channelType))
            {
                continue;
            }

            if (!_editionPolicy.IsChannelAllowed(channelType))
            {
                LogChannelNotAllowed(_logger, source, channelType, _options.Edition);
                continue;
            }

            // Skip a channel whose delivery circuit breaker is tripped open: enqueuing a dispatch
            // that is certain to dead-letter is exactly the storm this bounds. Recurring ops
            // notifications resume on the channel once a half-open probe confirms recovery.
            if (_circuitBreaker.IsOpen(channelType, now))
            {
                LogChannelCircuitOpen(_logger, source, channelType);
                continue;
            }

            builder.Add(channelType);
        }

        return builder.ToImmutable();
    }

    private static AlertEventEnvelope BuildEnvelope(OpsNotification notification)
    {
        var payload = new OpsAlertPayload
        {
            Source = notification.Source,
            Severity = notification.Severity.ToString(),
            Title = notification.Title,
            Body = notification.Body,
            Attributes = notification.Attributes,
        };

        return new AlertEventEnvelope
        {
            // Dedupe on (source, identifier): the same terminal event enqueues once.
            DedupeKey = $"ops:{notification.Source}:{notification.DedupeIdentifier}",
            RuleId = 0,
            ZoneId = null,
            // ServiceId carries the ops source; rule/zone/object/layer/trigger are inert
            // placeholders for ops events (see AlertEventEnvelope.Source docs).
            ServiceId = notification.Source,
            LayerId = 0,
            ObjectId = 0,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 0,
            Severity = notification.Severity,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(payload, OpsNotificationJsonContext.Default.OpsAlertPayload),
            IncidentStatus = AlertIncidentStatus.Started,
            IncidentDurationMs = 0,
            Source = AlertEventSources.Ops,
        };
    }

    [LoggerMessage(EventId = 9450, Level = LogLevel.Debug, Message = "Ops notification from {Source} ({Severity}) skipped: below minimum severity {MinSeverity}.")]
    private static partial void LogBelowMinSeverity(ILogger logger, string source, AlertSeverity severity, AlertSeverity minSeverity);

    [LoggerMessage(EventId = 9451, Level = LogLevel.Warning, Message = "Ops notification channel {ChannelType} for {Source} is not allowed by edition {Edition}; skipping that channel.")]
    private static partial void LogChannelNotAllowed(ILogger logger, string source, AlertChannelType channelType, AlertEdition edition);

    [LoggerMessage(EventId = 9453, Level = LogLevel.Warning, Message = "Ops notification channel {ChannelType} for {Source} skipped: the delivery circuit breaker is open (channel is persistently failing).")]
    private static partial void LogChannelCircuitOpen(ILogger logger, string source, AlertChannelType channelType);

    [LoggerMessage(EventId = 9452, Level = LogLevel.Information, Message = "Ops notification from {Source} ({Severity}) enqueued to {ChannelCount} channel(s) (dedupe {DedupeIdentifier}).")]
    private static partial void LogEnqueued(ILogger logger, string source, AlertSeverity severity, int channelCount, string dedupeIdentifier);

    [LoggerMessage(EventId = 9454, Level = LogLevel.Warning, Message = "Ops notification from {Source} ({Severity}) persisted without a deliverable channel (dedupe {DedupeIdentifier}).")]
    private static partial void LogPersistedWithoutChannel(ILogger logger, string source, AlertSeverity severity, string dedupeIdentifier);
}
