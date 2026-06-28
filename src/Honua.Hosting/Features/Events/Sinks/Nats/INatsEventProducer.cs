// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Minimal transport abstraction over a NATS JetStream producer (#357).
/// </summary>
/// <remarks>
/// The concrete <see cref="NatsJetStreamEventProducer"/> wraps the NATS.Net
/// client and owns all JetStream connection and deduplication configuration.
/// Keeping the sink dependent on this thin abstraction rather than the concrete
/// client means the dead-letter routing and serialization logic in
/// <see cref="NatsFeatureChangeEventSink"/> can be unit-tested without a live
/// JetStream server.
/// </remarks>
internal interface INatsEventProducer : IAsyncDisposable
{
    /// <summary>
    /// Publishes a single message to <paramref name="subject"/> through JetStream
    /// and awaits the server acknowledgement. Throws when the publish is not
    /// acknowledged (the sink turns a throw into dead-letter routing or a metered
    /// failure).
    /// </summary>
    /// <param name="subject">Destination JetStream subject.</param>
    /// <param name="messageId">
    /// Deduplication identifier published as the <c>Nats-Msg-Id</c> header. When
    /// non-empty, JetStream rejects a duplicate within the stream's dedup window,
    /// giving publish-side exactly-once delivery. Empty disables deduplication for
    /// the message.
    /// </param>
    /// <param name="value">Serialized event payload.</param>
    /// <param name="headers">Message headers (event id, operation, content-type, ...).</param>
    /// <param name="cancellationToken">Token signalling host shutdown.</param>
    Task PublishAsync(
        string subject,
        string messageId,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken = default);
}
