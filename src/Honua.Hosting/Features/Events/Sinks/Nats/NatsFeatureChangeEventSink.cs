// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Concrete <see cref="IFeatureChangeEventSink"/> that publishes committed
/// feature-change events to NATS JetStream (#357).
/// </summary>
/// <remarks>
/// Events are serialized as JSON and published to a single JetStream subject so
/// per-subject ordering preserves commit order. When deduplication is enabled the
/// message carries a <c>Nats-Msg-Id</c> equal to the event id, so JetStream
/// rejects a duplicate within the stream's dedup window (publish-side
/// exactly-once: no duplicate stored on retry). When a delivery still fails, the
/// event is routed to the configured dead-letter subject with diagnostic headers;
/// if dead-lettering is disabled or itself fails, the failure surfaces to the
/// broadcaster, which isolates and meters it without affecting durable storage or
/// sibling sinks.
/// </remarks>
internal sealed partial class NatsFeatureChangeEventSink : IFeatureChangeEventSink
{
    private const string ContentType = "application/json";

    private readonly INatsEventProducer _producer;
    private readonly NatsFeatureChangeEventSinkOptions _options;
    private readonly ILogger<NatsFeatureChangeEventSink> _logger;

    public NatsFeatureChangeEventSink(
        INatsEventProducer producer,
        IOptions<NatsFeatureChangeEventSinkOptions> options,
        ILogger<NatsFeatureChangeEventSink> logger)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "nats";

    /// <inheritdoc />
    public async Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureEvent);

        var messageId = _options.EnableDeduplication ? featureEvent.EventId : string.Empty;
        var value = JsonSerializer.SerializeToUtf8Bytes(
            featureEvent,
            FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
        var headers = BuildHeaders(featureEvent);

        try
        {
            await _producer
                .PublishAsync(_options.Subject, messageId, value, headers, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DeadLetterAsync(featureEvent, value, headers, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeadLetterAsync(
        FeatureChangeEvent featureEvent,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        Exception failure,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.DeadLetterSubject))
        {
            // Dead-lettering disabled: surface to the broadcaster, which isolates
            // and meters the failure without affecting durable storage or siblings.
            throw failure;
        }

        var dlqHeaders = new List<KeyValuePair<string, string>>(headers.Count + 3);
        dlqHeaders.AddRange(headers);
        dlqHeaders.Add(new("x-dlq-source-subject", _options.Subject));
        dlqHeaders.Add(new("x-dlq-error", Truncate(failure.Message, 1024)));
        dlqHeaders.Add(new("x-dlq-failed-at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

        // A distinct dedup id avoids the dead-letter copy colliding with the
        // primary event in the dead-letter stream's dedup window.
        var dlqMessageId = _options.EnableDeduplication
            ? string.Concat(featureEvent.EventId, ":dlq")
            : string.Empty;

        try
        {
            await _producer
                .PublishAsync(_options.DeadLetterSubject!, dlqMessageId, value, dlqHeaders, cancellationToken)
                .ConfigureAwait(false);
            FeatureChangeEventSinkMetrics.DeadLettered.Add(
                1,
                new KeyValuePair<string, object?>("sink", Name));
            LogDeadLettered(_logger, featureEvent.EventId, _options.DeadLetterSubject!, failure);
        }
        catch (Exception dlqEx)
        {
            // Both primary and dead-letter delivery failed: surface the original
            // failure so the broadcaster meters it. Record the DLQ failure too.
            LogDeadLetterFailed(_logger, featureEvent.EventId, _options.DeadLetterSubject!, dlqEx);
            throw failure;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildHeaders(FeatureChangeEvent featureEvent)
        =>
        [
            new("content-type", ContentType),
            new("x-event-id", featureEvent.EventId),
            new("x-event-cursor", featureEvent.Cursor.ToString(CultureInfo.InvariantCulture)),
            new("x-service-id", featureEvent.ServiceId),
            new("x-layer-id", featureEvent.LayerId.ToString(CultureInfo.InvariantCulture)),
            new("x-feature-id", featureEvent.ObjectId.ToString(CultureInfo.InvariantCulture)),
            new("x-operation", featureEvent.Operation),
            new("x-protocol", featureEvent.Protocol),
        ];

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(
        EventId = 9412,
        Level = LogLevel.Warning,
        Message = "NATS sink dead-lettered feature-change event {EventId} to subject '{DeadLetterSubject}' after a delivery failure.")]
    private static partial void LogDeadLettered(ILogger logger, string eventId, string deadLetterSubject, Exception failure);

    [LoggerMessage(
        EventId = 9413,
        Level = LogLevel.Error,
        Message = "NATS sink failed to dead-letter feature-change event {EventId} to subject '{DeadLetterSubject}'; the original delivery failure will be surfaced.")]
    private static partial void LogDeadLetterFailed(ILogger logger, string eventId, string deadLetterSubject, Exception failure);
}
