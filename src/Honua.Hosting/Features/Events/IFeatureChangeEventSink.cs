// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Broker-agnostic destination for committed feature-change events.
/// </summary>
/// <remarks>
/// Sinks are the extension point for enterprise event-bus integrations (#357):
/// concrete Kafka and NATS JetStream adapters implement this interface and are
/// registered alongside the in-process default. The canonical publish path
/// (<c>FeatureStreamPublisher</c>) durably appends an event, fans it out to live
/// streaming sessions, then hands the persisted event to every registered sink
/// through <see cref="FeatureChangeEventSinkBroadcaster"/>. Sinks therefore see
/// the same normalized envelope as replay and webhook delivery and never observe
/// an event that was not durably stored.
/// <para>
/// Implementations must be resilient: a sink that throws must not break the
/// originating write, the durable append, live streaming, or sibling sinks. The
/// broadcaster isolates per-sink failures and runs sinks off the write hot path.
/// </para>
/// </remarks>
internal interface IFeatureChangeEventSink
{
    /// <summary>
    /// Gets a stable identifier used in diagnostics and metrics (for example
    /// <c>kafka</c>, <c>nats</c>, or <c>noop</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Publishes a durably persisted feature-change event to the underlying
    /// transport. Called after the event has been appended to the event store
    /// and broadcast to live sessions.
    /// </summary>
    /// <param name="featureEvent">The persisted, normalized feature-change event.</param>
    /// <param name="cancellationToken">A token that signals host shutdown.</param>
    Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default);
}
