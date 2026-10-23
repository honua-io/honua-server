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
    /// <summary>Counts the Things matching the query's filter, ignoring paging and ordering.</summary>
    Task<long> CountThingsAsync(CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Counts the Sensors matching the query's filter, ignoring paging and ordering.</summary>
    Task<long> CountSensorsAsync(CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Counts the ObservedProperties matching the query's filter, ignoring paging and ordering.</summary>
    Task<long> CountObservedPropertiesAsync(CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Counts the Datastreams matching the query's filter, ignoring paging and ordering.</summary>
    Task<long> CountDatastreamsAsync(CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Counts observations matching the datastream and filter, ignoring paging and ordering.</summary>
    Task<long> CountObservationsAsync(ObservationQuery query, CancellationToken cancellationToken);

    /// <summary>Lists the datastreams matching the query's filter, ordering, and paging.</summary>
    /// <param name="query">The filter, ordering, and paging to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SensorThingsDatastream>> ListDatastreamsAsync(
        CatalogQuery query,
        CancellationToken cancellationToken);

    /// <summary>Gets a single datastream by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsDatastream?> GetDatastreamAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists the Things matching the query's filter, ordering, and paging.</summary>
    Task<IReadOnlyList<SensorThingsThing>> ListThingsAsync(CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Gets a single Thing by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsThing?> GetThingAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists the Sensors matching the query's filter, ordering, and paging.</summary>
    Task<IReadOnlyList<SensorThingsSensor>> ListSensorsAsync(CatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Gets a single Sensor by identifier, or <see langword="null"/> if absent.</summary>
    Task<SensorThingsSensor?> GetSensorAsync(long id, CancellationToken cancellationToken);

    /// <summary>Lists the ObservedProperties matching the query's filter, ordering, and paging.</summary>
    Task<IReadOnlyList<SensorThingsObservedProperty>> ListObservedPropertiesAsync(
        CatalogQuery query,
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

    /// <summary>
    /// Ingests a batch of observations into the time-series store (Phase 2). Each input
    /// row is assigned a server-generated identifier. The datastream referenced by
    /// <see cref="ObservationIngestRow.DatastreamId"/> must already exist.
    /// </summary>
    /// <param name="rows">The observations to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted observations, in input order, with assigned identifiers.</returns>
    Task<IReadOnlyList<SensorThingsObservation>> IngestObservationsAsync(
        IReadOnlyList<ObservationIngestRow> rows,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new Datastream catalog entity and (when needed) the related Thing,
    /// Sensor, and ObservedProperty entities referenced by it (Phase 2). Identifiers
    /// referenced by the request that do not exist are created from the inline
    /// definitions carried on <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The datastream-creation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created datastream.</returns>
    Task<SensorThingsDatastream> CreateDatastreamAsync(
        CreateDatastreamRequest request,
        CancellationToken cancellationToken);
}
