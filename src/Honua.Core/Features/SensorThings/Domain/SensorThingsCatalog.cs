// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.SensorThings.Domain;

/// <summary>
/// Canonical (protocol-neutral) catalog record for an OGC SensorThings API
/// <c>Datastream</c> and its directly related catalog entities (Thing, Sensor,
/// ObservedProperty). A Datastream is the time-series-backed resource that ties a
/// <see cref="ThingId"/>, <see cref="SensorId"/>, and <see cref="ObservedPropertyId"/>
/// together and binds them to the rows stored in the observations time-series store.
/// </summary>
public sealed record SensorThingsDatastream
{
    /// <summary>Stable numeric identifier (<c>@iot.id</c>) of the datastream.</summary>
    public required long Id { get; init; }

    /// <summary>Human-readable name of the datastream.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the datastream.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The OGC Observation &amp; Measurement (O&amp;M) observation type URI, e.g.
    /// <c>http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement</c>.
    /// </summary>
    public required string ObservationType { get; init; }

    /// <summary>Unit-of-measurement display name (e.g. <c>degree Celsius</c>).</summary>
    public required string UnitName { get; init; }

    /// <summary>Unit-of-measurement symbol (e.g. <c>°C</c>).</summary>
    public required string UnitSymbol { get; init; }

    /// <summary>Unit-of-measurement definition URI (UCUM or QUDT).</summary>
    public required string UnitDefinition { get; init; }

    /// <summary>Identifier of the related Thing (device).</summary>
    public required long ThingId { get; init; }

    /// <summary>Identifier of the related Sensor (measurement instrument).</summary>
    public required long SensorId { get; init; }

    /// <summary>Identifier of the related ObservedProperty.</summary>
    public required long ObservedPropertyId { get; init; }

    /// <summary>Inclusive start of the datastream phenomenon-time extent, if known.</summary>
    public DateTimeOffset? PhenomenonTimeStart { get; init; }

    /// <summary>Inclusive end of the datastream phenomenon-time extent, if known.</summary>
    public DateTimeOffset? PhenomenonTimeEnd { get; init; }
}

/// <summary>
/// Canonical catalog record for an OGC SensorThings API <c>Thing</c> (a physical
/// device or object whose state is monitored by one or more datastreams).
/// </summary>
public sealed record SensorThingsThing
{
    /// <summary>Stable numeric identifier (<c>@iot.id</c>) of the Thing.</summary>
    public required long Id { get; init; }

    /// <summary>Human-readable name of the Thing.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the Thing.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// Canonical catalog record for an OGC SensorThings API <c>Sensor</c>
/// (a measurement instrument that produces observations).
/// </summary>
public sealed record SensorThingsSensor
{
    /// <summary>Stable numeric identifier (<c>@iot.id</c>) of the Sensor.</summary>
    public required long Id { get; init; }

    /// <summary>Human-readable name of the Sensor.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the Sensor.</summary>
    public required string Description { get; init; }

    /// <summary>Encoding type of the <see cref="Metadata"/> (e.g. <c>application/pdf</c>).</summary>
    public required string EncodingType { get; init; }

    /// <summary>Sensor metadata (a URL or inline description per the encoding type).</summary>
    public required string Metadata { get; init; }
}

/// <summary>
/// Canonical catalog record for an OGC SensorThings API <c>ObservedProperty</c>
/// (the phenomenon being measured, e.g. air temperature).
/// </summary>
public sealed record SensorThingsObservedProperty
{
    /// <summary>Stable numeric identifier (<c>@iot.id</c>) of the ObservedProperty.</summary>
    public required long Id { get; init; }

    /// <summary>Human-readable name of the ObservedProperty.</summary>
    public required string Name { get; init; }

    /// <summary>URI definition of the observed phenomenon.</summary>
    public required string Definition { get; init; }

    /// <summary>Human-readable description of the ObservedProperty.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// Canonical record for an OGC SensorThings API <c>Observation</c> — a single
/// time-stamped measurement produced by a datastream. Observations are stored in
/// the dedicated time-series store rather than the catalog.
/// </summary>
public sealed record SensorThingsObservation
{
    /// <summary>Stable numeric identifier (<c>@iot.id</c>) of the observation.</summary>
    public required long Id { get; init; }

    /// <summary>Identifier of the owning datastream.</summary>
    public required long DatastreamId { get; init; }

    /// <summary>The time instant when the observed phenomenon occurred.</summary>
    public required DateTimeOffset PhenomenonTime { get; init; }

    /// <summary>The time instant when the result was generated, if distinct.</summary>
    public DateTimeOffset? ResultTime { get; init; }

    /// <summary>The numeric measurement result.</summary>
    public required double Result { get; init; }

    /// <summary>Identifier of the related FeatureOfInterest, if any.</summary>
    public long? FeatureOfInterestId { get; init; }
}

/// <summary>
/// Time-range and result-range filter applied to an observations query. All bounds
/// are optional and inclusive of the lower / exclusive of the upper bound where set.
/// </summary>
/// <param name="DatastreamId">Restrict to a single datastream when set.</param>
/// <param name="WhereSql">A parameterized SQL WHERE fragment translated from a STA <c>$filter</c>.</param>
/// <param name="WhereParameters">Ordered parameter values for <paramref name="WhereSql"/> placeholders <c>@p0..@pN</c>.</param>
/// <param name="OrderByDescending">When true order by <c>phenomenonTime</c> descending; otherwise ascending.</param>
/// <param name="Skip">Number of leading rows to skip (<c>$skip</c>).</param>
/// <param name="Top">Maximum number of rows to return (<c>$top</c>); the caller clamps this.</param>
public readonly record struct ObservationQuery(
    long? DatastreamId,
    string? WhereSql,
    IReadOnlyList<object?> WhereParameters,
    bool OrderByDescending,
    int Skip,
    int Top);

/// <summary>
/// A single observation row supplied to <c>IObservationStore.IngestObservationsAsync</c>
/// (Phase 2). The identifier is assigned by the store, so it is not carried here.
/// </summary>
/// <param name="DatastreamId">Identifier of the owning datastream.</param>
/// <param name="PhenomenonTime">When the observed phenomenon occurred.</param>
/// <param name="ResultTime">When the result was generated, if distinct.</param>
/// <param name="Result">The numeric measurement result.</param>
/// <param name="FeatureOfInterestId">Identifier of the related FeatureOfInterest, if any.</param>
public readonly record struct ObservationIngestRow(
    long DatastreamId,
    DateTimeOffset PhenomenonTime,
    DateTimeOffset? ResultTime,
    double Result,
    long? FeatureOfInterestId);

/// <summary>
/// Inline definition of a related catalog entity referenced by a
/// <see cref="CreateDatastreamRequest"/>. When <see cref="Id"/> resolves to an existing
/// row the existing entity is reused; otherwise a new row is created from these fields.
/// </summary>
/// <param name="Id">Stable identifier of the related entity. When 0, the store assigns one.</param>
/// <param name="Name">Entity name (used only when creating a new entity).</param>
/// <param name="Description">Entity description (used only when creating a new entity).</param>
public readonly record struct RelatedEntityRef(long Id, string? Name, string? Description);

/// <summary>
/// Request to create a SensorThings <c>Datastream</c> and, when needed, the related
/// Thing, Sensor, and ObservedProperty entities (Phase 2 <c>datastream.create</c>).
/// </summary>
public sealed record CreateDatastreamRequest
{
    /// <summary>Datastream name.</summary>
    public required string Name { get; init; }

    /// <summary>Datastream description.</summary>
    public required string Description { get; init; }

    /// <summary>The O&amp;M observation type URI.</summary>
    public string ObservationType { get; init; } =
        "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement";

    /// <summary>Unit-of-measurement display name.</summary>
    public required string UnitName { get; init; }

    /// <summary>Unit-of-measurement symbol.</summary>
    public required string UnitSymbol { get; init; }

    /// <summary>Unit-of-measurement definition URI.</summary>
    public required string UnitDefinition { get; init; }

    /// <summary>The related Thing (created when its id does not already exist).</summary>
    public required RelatedEntityRef Thing { get; init; }

    /// <summary>The related Sensor (created when its id does not already exist).</summary>
    public required RelatedEntityRef Sensor { get; init; }

    /// <summary>The related ObservedProperty (created when its id does not already exist).</summary>
    public required RelatedEntityRef ObservedProperty { get; init; }
}
