// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.SensorThings.Domain;

public sealed record SensorThingsDatastream
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string ObservationType { get; init; }

    public required string? UnitName { get; init; }

    public required string? UnitSymbol { get; init; }

    public required string? UnitDefinition { get; init; }

    public required long ThingId { get; init; }

    public required long SensorId { get; init; }

    public required long ObservedPropertyId { get; init; }

    public DateTimeOffset? PhenomenonTimeStart { get; init; }

    public DateTimeOffset? PhenomenonTimeEnd { get; init; }
}

public sealed record SensorThingsThing
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }
}

public sealed record SensorThingsSensor
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string EncodingType { get; init; }

    public required string Metadata { get; init; }

    public System.Text.Json.JsonElement? JsonMetadata { get; init; }
}

public sealed record SensorThingsObservedProperty
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    public required string Definition { get; init; }

    public required string Description { get; init; }
}

public sealed record SensorThingsObservation
{
    public required long Id { get; init; }

    public required long DatastreamId { get; init; }

    public required DateTimeOffset PhenomenonTime { get; init; }

    public DateTimeOffset? ResultTime { get; init; }

    public required double? Result { get; init; }

    public System.Text.Json.JsonElement? JsonResult { get; init; }

    public DateTimeOffset? PhenomenonTimeEnd { get; init; }

    public long? FeatureOfInterestId { get; init; }
}

// The filter fragment is built by the server over allowed columns and is never request text.
public readonly record struct ObservationQuery(
    long? DatastreamId,
    string? WhereSql,
    IReadOnlyList<object?> WhereParameters,
    string? OrderBySql,
    int Skip,
    int Top);

public readonly record struct CatalogQuery(
    string? WhereSql,
    IReadOnlyList<object?> WhereParameters,
    string? OrderBySql,
    int Skip,
    int Top)
{
    public long? DatastreamThingId { get; init; }

    public long? DatastreamSensorId { get; init; }

    public long? DatastreamObservedPropertyId { get; init; }

    public static CatalogQuery Page(int skip, int top) =>
        new(null, Array.Empty<object?>(), null, skip, top);
}

public readonly record struct ObservationIngestRow(
    long DatastreamId,
    DateTimeOffset PhenomenonTime,
    DateTimeOffset? ResultTime,
    double Result,
    long? FeatureOfInterestId);

public readonly record struct RelatedEntityRef(long Id, string? Name, string? Description,
    string? EncodingType = null, System.Text.Json.JsonElement? Metadata = null, string? Definition = null);

public sealed record CreateDatastreamRequest
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public string ObservationType { get; init; } =
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement";

    public required string? UnitName { get; init; }

    public required string? UnitSymbol { get; init; }

    public required string? UnitDefinition { get; init; }

    public required RelatedEntityRef Thing { get; init; }

    public required RelatedEntityRef Sensor { get; init; }

    public required RelatedEntityRef ObservedProperty { get; init; }
}
