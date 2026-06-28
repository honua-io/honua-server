// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Minimal transport abstraction over a Kafka producer (#357).
/// </summary>
/// <remarks>
/// The concrete <see cref="ConfluentKafkaEventProducer"/> wraps the Confluent
/// client and owns all idempotent-producer configuration. Keeping the sink
/// dependent on this thin abstraction rather than the concrete client means the
/// dead-letter routing and serialization logic in <see cref="KafkaFeatureChangeEventSink"/>
/// can be unit-tested without a live broker.
/// </remarks>
internal interface IKafkaEventProducer : IAsyncDisposable
{
    /// <summary>
    /// Produces a single message to <paramref name="topic"/> and awaits broker
    /// acknowledgement. Throws when delivery cannot be confirmed (the sink turns
    /// a throw into dead-letter routing or a metered failure).
    /// </summary>
    /// <param name="topic">Destination topic.</param>
    /// <param name="key">Partition key; preserves per-feature ordering.</param>
    /// <param name="value">Serialized event payload.</param>
    /// <param name="headers">Message headers (event id, operation, content-type, ...).</param>
    /// <param name="cancellationToken">Token signalling host shutdown.</param>
    Task ProduceAsync(
        string topic,
        string key,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken = default);
}
