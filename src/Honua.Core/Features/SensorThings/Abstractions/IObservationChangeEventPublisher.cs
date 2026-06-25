// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Core.Features.SensorThings.Abstractions;

/// <summary>
/// Publishes newly-ingested SensorThings observations to real-time stream subscribers
/// (Phase 3). The SensorThings ingest path (Phase 2) calls this after a successful write
/// so the observation-stream transport (SSE / WebSocket) can fan it out to connected
/// clients. The default no-op implementation keeps ingest decoupled from streaming when
/// the streaming surface is not registered.
/// </summary>
public interface IObservationChangeEventPublisher
{
    /// <summary>
    /// Publishes a batch of newly-created observations. Implementations must not throw;
    /// streaming is best-effort and must never fail the ingest write.
    /// </summary>
    /// <param name="observations">The observations that were persisted.</param>
    void PublishObservations(IReadOnlyList<SensorThingsObservation> observations);
}

/// <summary>
/// Default <see cref="IObservationChangeEventPublisher"/> that discards events. Registered
/// when no streaming transport is wired so the ingest path always has a publisher to call.
/// </summary>
public sealed class NullObservationChangeEventPublisher : IObservationChangeEventPublisher
{
    /// <summary>Shared instance.</summary>
    public static NullObservationChangeEventPublisher Instance { get; } = new();

    /// <inheritdoc />
    public void PublishObservations(IReadOnlyList<SensorThingsObservation> observations)
    {
        // Intentionally empty: no streaming transport registered.
    }
}
