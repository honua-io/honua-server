// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Default <see cref="IFeatureChangeEventSink"/> that discards events.
/// </summary>
/// <remarks>
/// Registered so the sink fan-out path is exercised end-to-end (and observable
/// via metrics) even when no broker adapter is configured. Concrete Kafka and
/// NATS adapters (#357) are additive: they register alongside this sink and the
/// broadcaster delivers to all of them.
/// </remarks>
internal sealed class NoOpFeatureChangeEventSink : IFeatureChangeEventSink
{
    /// <inheritdoc />
    public string Name => "noop";

    /// <inheritdoc />
    public Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
