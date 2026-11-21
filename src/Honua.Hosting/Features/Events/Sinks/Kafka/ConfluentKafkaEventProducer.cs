// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

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

        ApplySecurity(config, value);
        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    // Re-checked here because the producer can be built from options that never went through
    // ValidateOnStart; a misconfigured protocol must refuse to connect rather than fall back to PLAINTEXT.
    private static void ApplySecurity(ProducerConfig config, KafkaFeatureChangeEventSinkOptions value)
    {
        var failures = KafkaFeatureChangeEventSinkOptionsValidator.SecurityFailures(value);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", failures));
        }

        if (KafkaSecurityNames.NormalizeProtocol(value.SecurityProtocol) is { } protocol)
        {
            config.SecurityProtocol = Enum.Parse<SecurityProtocol>(protocol, ignoreCase: true);
        }

        if (KafkaSecurityNames.NormalizeMechanism(value.SaslMechanism) is { } mechanism)
        {
            config.SaslMechanism = Enum.Parse<SaslMechanism>(mechanism, ignoreCase: true);
        }

        if (!string.IsNullOrWhiteSpace(value.SaslUsername))
        {
            config.SaslUsername = value.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(value.SaslPassword))
        {
            config.SaslPassword = value.SaslPassword;
        }
    }

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

    public ValueTask DisposeAsync()
    {
        // Flush in-flight messages so no acknowledged-but-unsent records are lost
        // on shutdown, then release the native client handle.
        _producer.Flush(FlushTimeout);
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
