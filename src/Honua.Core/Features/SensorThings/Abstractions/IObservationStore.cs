// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Core.Features.SensorThings.Abstractions;

/// <summary>
/// Read access to the OGC SensorThings API observations time-series store and the
/// associated catalog entities (Datastreams, Things, Sensors, ObservedProperties).
/// Phase 1 exposes the read surface only; ingest is added in a later phase.
/// </summary>
public interface IObservationStore
{
    /// <summary>Lists all datastreams in the catalog.</summary>
    /// <param name="skip">Number of leading rows to skip.</param>
    /// <param name="top">Maximum rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SensorThingsDatastream>> ListDatastreamsAsync(
        int skip,
        int top,
        CancellationToken cancellationToken);

    /// <summary>Gets a single datastream by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsDatastream?> GetDatastreamAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists all Things in the catalog.</summary>
    Task<IReadOnlyList<SensorThingsThing>> ListThingsAsync(int skip, int top, CancellationToken cancellationToken);

    /// <summary>Gets a single Thing by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsThing?> GetThingAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists all Sensors in the catalog.</summary>
    Task<IReadOnlyList<SensorThingsSensor>> ListSensorsAsync(int skip, int top, CancellationToken cancellationToken);

    /// <summary>Gets a single Sensor by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsSensor?> GetSensorAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists all ObservedProperties in the catalog.</summary>
    Task<IReadOnlyList<SensorThingsObservedProperty>> ListObservedPropertiesAsync(
        int skip,
        int top,
        CancellationToken cancellationToken);

    /// <summary>Gets a single ObservedProperty by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsObservedProperty?> GetObservedPropertyAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Queries the observations time-series store applying the time/result filter,
    /// ordering, and paging carried by <paramref name="query"/>.
    /// </summary>
    Task<IReadOnlyList<SensorThingsObservation>> QueryObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken);

    /// <summary>Gets a single observation by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsObservation?> GetObservationAsync(long id, CancellationToken cancellationToken);
}
