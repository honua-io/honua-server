// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Confluent-backed <see cref="IKafkaEventProducer"/> that owns all
/// idempotent-producer configuration (#357).
/// </summary>
/// <remarks>
/// This is the only type that references the Confluent client. It is constructed
/// once as a singleton; the underlying <see cref="IProducer{TKey,TValue}"/> is
/// thread-safe and shared across all publishes. When idempotence is enabled the
/// client enforces <c>acks=all</c>, a bounded in-flight window, and infinite
/// retries, giving producer-side exactly-once delivery (no duplicates on retry).
/// </remarks>
internal sealed class ConfluentKafkaEventProducer : IKafkaEventProducer
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    private readonly IProducer<string, byte[]> _producer;

    public ConfluentKafkaEventProducer(IOptions<KafkaFeatureChangeEventSinkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value ?? throw new ArgumentNullException(nameof(options));

        var config = new ProducerConfig
        {
            BootstrapServers = value.BootstrapServers,
            EnableIdempotence = value.EnableIdempotence,
            // Idempotence implies acks=all; set explicitly so the intent is clear
            // even when idempotence is disabled.
            Acks = value.EnableIdempotence ? Acks.All : Acks.Leader,
            MessageTimeoutMs = value.MessageTimeoutMs,
        };

        if (!string.IsNullOrWhiteSpace(value.SecurityProtocol) &&
            Enum.TryParse<SecurityProtocol>(value.SecurityProtocol, ignoreCase: true, out var protocol))
        {
            config.SecurityProtocol = protocol;
        }

        if (!string.IsNullOrWhiteSpace(value.SaslMechanism) &&
            Enum.TryParse<SaslMechanism>(value.SaslMechanism, ignoreCase: true, out var mechanism))
        {
            config.SaslMechanism = mechanism;
        }

        if (!string.IsNullOrWhiteSpace(value.SaslUsername))
        {
            config.SaslUsername = value.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(value.SaslPassword))
        {
            config.SaslPassword = value.SaslPassword;
        }

        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    /// <inheritdoc />
    public async Task ProduceAsync(
        string topic,
        string key,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var message = new Message<string, byte[]>
        {
            Key = key,
            Value = value,
            Headers = BuildHeaders(headers),
        };

        await _producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
    }

    private static Headers BuildHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var kafkaHeaders = new Headers();
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            kafkaHeaders.Add(header.Key, Encoding.UTF8.GetBytes(header.Value ?? string.Empty));
        }

        return kafkaHeaders;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Flush in-flight messages so no acknowledged-but-unsent records are lost
        // on shutdown, then release the native client handle.
        _producer.Flush(FlushTimeout);
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
