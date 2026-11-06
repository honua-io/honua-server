// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Core.Features.SensorThings.Abstractions;

public interface IObservationStore
{
    Task<long> CountThingsAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<long> CountSensorsAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<long> CountObservedPropertiesAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<long> CountDatastreamsAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<long> CountObservationsAsync(ObservationQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorThingsDatastream>> ListDatastreamsAsync(
        CatalogQuery query,
        CancellationToken cancellationToken);

    Task<SensorThingsDatastream?> GetDatastreamAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorThingsThing>> ListThingsAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<SensorThingsThing?> GetThingAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorThingsSensor>> ListSensorsAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<SensorThingsSensor?> GetSensorAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorThingsObservedProperty>> ListObservedPropertiesAsync(
        CatalogQuery query,
        CancellationToken cancellationToken);

    Task<SensorThingsObservedProperty?> GetObservedPropertyAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorThingsObservation>> QueryObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken);

    Task<SensorThingsObservation?> GetObservationAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SensorThingsObservation>> IngestObservationsAsync(
        IReadOnlyList<ObservationIngestRow> rows,
        CancellationToken cancellationToken);

    Task<SensorThingsDatastream> CreateDatastreamAsync(
        CreateDatastreamRequest request,
        CancellationToken cancellationToken);
}
