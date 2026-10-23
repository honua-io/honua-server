// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Core.Features.SensorThings.Abstractions;

/// <summary>
/// Publishes newly-ingested SensorThings observations to real-time stream subscribers.
/// The SensorThings ingest path calls this after a successful write so the
/// observation-stream transport can fan observations out to connected clients.
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
