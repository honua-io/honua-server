// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Concrete <see cref="IFeatureChangeEventSink"/> that publishes committed
/// feature-change events to Apache Kafka (#357).
/// </summary>
/// <remarks>
/// Events are serialized as JSON and keyed by <c>service/layer/feature</c> so all
/// mutations to the same feature land on the same partition and preserve order.
/// The underlying producer is idempotent (producer-side exactly-once) so a
/// broker-acknowledged message is never duplicated on retry. When a delivery
/// still fails after the producer's own retries, the event is routed to the
/// configured dead-letter topic with diagnostic headers; if dead-lettering is
/// disabled or itself fails, the failure surfaces to the broadcaster, which
/// isolates and meters it without affecting durable storage or sibling sinks.
/// </remarks>
internal sealed partial class KafkaFeatureChangeEventSink : IFeatureChangeEventSink
{
    private const string ContentType = "application/json";

    private readonly IKafkaEventProducer _producer;
    private readonly KafkaFeatureChangeEventSinkOptions _options;
    private readonly ILogger<KafkaFeatureChangeEventSink> _logger;

    public KafkaFeatureChangeEventSink(
        IKafkaEventProducer producer,
        IOptions<KafkaFeatureChangeEventSinkOptions> options,
        ILogger<KafkaFeatureChangeEventSink> logger)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "kafka";

    /// <inheritdoc />
    public async Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureEvent);

        var key = BuildKey(featureEvent);
        var value = JsonSerializer.SerializeToUtf8Bytes(
            featureEvent,
            FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
        var headers = BuildHeaders(featureEvent);

        try
        {
            await _producer
                .ProduceAsync(_options.Topic, key, value, headers, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DeadLetterAsync(featureEvent, key, value, headers, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeadLetterAsync(
        FeatureChangeEvent featureEvent,
        string key,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        Exception failure,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.DeadLetterTopic))
        {
            // Dead-lettering disabled: surface to the broadcaster, which isolates
            // and meters the failure without affecting durable storage or siblings.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return; // unreachable; satisfies the compiler
        }

        var dlqHeaders = new List<KeyValuePair<string, string>>(headers.Count + 3);
        dlqHeaders.AddRange(headers);
        dlqHeaders.Add(new("x-dlq-source-topic", _options.Topic));
        dlqHeaders.Add(new("x-dlq-error", Truncate(failure.Message, 1024)));
        dlqHeaders.Add(new("x-dlq-failed-at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

        try
        {
            await _producer
                .ProduceAsync(_options.DeadLetterTopic!, key, value, dlqHeaders, cancellationToken)
                .ConfigureAwait(false);
            FeatureChangeEventSinkMetrics.DeadLettered.Add(
                1,
                new KeyValuePair<string, object?>("sink", Name));
            LogDeadLettered(_logger, featureEvent.EventId, _options.DeadLetterTopic!, failure);
        }
        catch (Exception dlqEx)
        {
            // Both primary and dead-letter delivery failed: surface the original
            // failure so the broadcaster meters it. Record the DLQ failure too.
            LogDeadLetterFailed(_logger, featureEvent.EventId, _options.DeadLetterTopic!, dlqEx);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw; // unreachable; satisfies the compiler
        }
    }

    private static string BuildKey(FeatureChangeEvent featureEvent)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{featureEvent.ServiceId}/{featureEvent.LayerId}/{featureEvent.ObjectId}");

    private static IReadOnlyList<KeyValuePair<string, string>> BuildHeaders(FeatureChangeEvent featureEvent)
        =>
        [
            new("content-type", ContentType),
            new("x-event-id", featureEvent.EventId),
            new("x-event-cursor", featureEvent.Cursor.ToString(CultureInfo.InvariantCulture)),
            new("x-service-id", featureEvent.ServiceId),
            new("x-layer-id", featureEvent.LayerId.ToString(CultureInfo.InvariantCulture)),
            new("x-operation", featureEvent.Operation),
            new("x-protocol", featureEvent.Protocol),
        ];

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(
        EventId = 9410,
        Level = LogLevel.Warning,
        Message = "Kafka sink dead-lettered feature-change event {EventId} to topic '{DeadLetterTopic}' after a delivery failure.")]
    private static partial void LogDeadLettered(ILogger logger, string eventId, string deadLetterTopic, Exception failure);

    [LoggerMessage(
        EventId = 9411,
        Level = LogLevel.Error,
        Message = "Kafka sink failed to dead-letter feature-change event {EventId} to topic '{DeadLetterTopic}'; the original delivery failure will be surfaced.")]
    private static partial void LogDeadLetterFailed(ILogger logger, string eventId, string deadLetterTopic, Exception failure);
}
