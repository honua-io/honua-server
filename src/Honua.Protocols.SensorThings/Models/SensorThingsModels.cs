// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.SensorThings.Models;

/// <summary>
/// STA v1.1 entity-collection envelope: a <c>value</c> array plus optional
/// <c>@iot.count</c> and <c>@iot.nextLink</c> paging members.
/// </summary>
/// <typeparam name="T">The STA entity DTO type carried by the collection.</typeparam>
public sealed record StaEntitySet<T>
{
    /// <summary>Total matching entity count (present when <c>$count=true</c>).</summary>
    [JsonPropertyName("@iot.count")]
    public long? Count { get; init; }

    /// <summary>The entities in this page.</summary>
    [JsonPropertyName("value")]
    public required IReadOnlyList<T> Value { get; init; }

    /// <summary>Absolute link to the next page of results, when one exists.</summary>
    [JsonPropertyName("@iot.nextLink")]
    public string? NextLink { get; init; }
}

/// <summary>STA v1.1 <c>Thing</c> entity DTO.</summary>
public sealed record StaThing
{
    /// <summary>Entity identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    /// <summary>Canonical self link (<c>@iot.selfLink</c>).</summary>
    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    /// <summary>Thing name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Thing description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Free-form properties bag (empty in Phase 1).</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>Navigation link to the Thing's Datastreams.</summary>
    [JsonPropertyName("Datastreams@iot.navigationLink")]
    public required string DatastreamsNavigationLink { get; init; }
}

/// <summary>STA v1.1 <c>Sensor</c> entity DTO.</summary>
public sealed record StaSensor
{
    /// <summary>Entity identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    /// <summary>Canonical self link (<c>@iot.selfLink</c>).</summary>
    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    /// <summary>Sensor name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Sensor description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Encoding type of <see cref="Metadata"/>.</summary>
    [JsonPropertyName("encodingType")]
    public required string EncodingType { get; init; }

    /// <summary>Sensor metadata (URL or inline per the encoding type).</summary>
    [JsonPropertyName("metadata")]
    public required string Metadata { get; init; }

    /// <summary>Navigation link to the Sensor's Datastreams.</summary>
    [JsonPropertyName("Datastreams@iot.navigationLink")]
    public required string DatastreamsNavigationLink { get; init; }
}

/// <summary>STA v1.1 <c>ObservedProperty</c> entity DTO.</summary>
public sealed record StaObservedProperty
{
    /// <summary>Entity identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    /// <summary>Canonical self link (<c>@iot.selfLink</c>).</summary>
    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    /// <summary>ObservedProperty name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>URI definition of the observed phenomenon.</summary>
    [JsonPropertyName("definition")]
    public required string Definition { get; init; }

    /// <summary>ObservedProperty description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Navigation link to the ObservedProperty's Datastreams.</summary>
    [JsonPropertyName("Datastreams@iot.navigationLink")]
    public required string DatastreamsNavigationLink { get; init; }
}

/// <summary>STA v1.1 unit-of-measurement object embedded in a Datastream.</summary>
public sealed record StaUnitOfMeasurement
{
    /// <summary>Display name (e.g. <c>degree Celsius</c>).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Symbol (e.g. <c>°C</c>).</summary>
    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    /// <summary>Definition URI (UCUM or QUDT).</summary>
    [JsonPropertyName("definition")]
    public required string Definition { get; init; }
}

/// <summary>STA v1.1 <c>Datastream</c> entity DTO.</summary>
public sealed record StaDatastream
{
    /// <summary>Entity identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    /// <summary>Canonical self link (<c>@iot.selfLink</c>).</summary>
    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    /// <summary>Datastream name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Datastream description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>The O&amp;M observation type URI.</summary>
    [JsonPropertyName("observationType")]
    public required string ObservationType { get; init; }

    /// <summary>Unit of measurement for the datastream's results.</summary>
    [JsonPropertyName("unitOfMeasurement")]
    public required StaUnitOfMeasurement UnitOfMeasurement { get; init; }

    /// <summary>Phenomenon-time extent as an ISO-8601 interval, when known.</summary>
    [JsonPropertyName("phenomenonTime")]
    public string? PhenomenonTime { get; init; }

    /// <summary>Navigation link to the Datastream's Observations.</summary>
    [JsonPropertyName("Observations@iot.navigationLink")]
    public required string ObservationsNavigationLink { get; init; }

    /// <summary>Navigation link to the related Thing.</summary>
    [JsonPropertyName("Thing@iot.navigationLink")]
    public required string ThingNavigationLink { get; init; }

    /// <summary>Navigation link to the related Sensor.</summary>
    [JsonPropertyName("Sensor@iot.navigationLink")]
    public required string SensorNavigationLink { get; init; }

    /// <summary>Navigation link to the related ObservedProperty.</summary>
    [JsonPropertyName("ObservedProperty@iot.navigationLink")]
    public required string ObservedPropertyNavigationLink { get; init; }

    /// <summary>Inline-expanded Observations (present only when <c>$expand=Observations</c>).</summary>
    [JsonPropertyName("Observations")]
    public IReadOnlyList<StaObservation>? Observations { get; init; }

    /// <summary>Inline-expanded Thing (present only when <c>$expand=Thing</c>).</summary>
    [JsonPropertyName("Thing")]
    public StaThing? Thing { get; init; }

    /// <summary>Inline-expanded Sensor (present only when <c>$expand=Sensor</c>).</summary>
    [JsonPropertyName("Sensor")]
    public StaSensor? Sensor { get; init; }

    /// <summary>Inline-expanded ObservedProperty (present only when <c>$expand=ObservedProperty</c>).</summary>
    [JsonPropertyName("ObservedProperty")]
    public StaObservedProperty? ObservedProperty { get; init; }
}

/// <summary>
/// Request body for STA Observation ingest (Phase 2). Posted either to
/// <c>/sta/v1.1/Observations</c> (with <see cref="Datastream"/> carrying the target
/// id) or to <c>/sta/v1.1/Datastreams({id})/Observations</c> (datastream taken from
/// the route).
/// </summary>
public sealed record StaObservationCreate
{
    /// <summary>When the phenomenon occurred (ISO-8601). Defaults to now when omitted.</summary>
    [JsonPropertyName("phenomenonTime")]
    public string? PhenomenonTime { get; init; }

    /// <summary>When the result was generated (ISO-8601), if distinct.</summary>
    [JsonPropertyName("resultTime")]
    public string? ResultTime { get; init; }

    /// <summary>The numeric measurement result.</summary>
    [JsonPropertyName("result")]
    public required double Result { get; init; }

    /// <summary>The owning Datastream reference (carries the target <c>@iot.id</c>).</summary>
    [JsonPropertyName("Datastream")]
    public StaEntityReference? Datastream { get; init; }
}

/// <summary>A SensorThings deep-insert / link reference carrying a target entity id.</summary>
public sealed record StaEntityReference
{
    /// <summary>The referenced entity identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public long IotId { get; init; }

    /// <summary>Optional name used when the referenced entity is created inline.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Optional description used when the referenced entity is created inline.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// Bulk ingest envelope: a <c>value</c> array of observations posted in one request to
/// <c>/sta/v1.1/Observations</c>. Honua extension to the single-create body for efficient
/// IoT ingest (<c>sensor.ingest</c> operation).
/// </summary>
public sealed record StaObservationBulkCreate
{
    /// <summary>The observations to ingest.</summary>
    [JsonPropertyName("value")]
    public required IReadOnlyList<StaObservationCreate> Value { get; init; }
}

/// <summary>Result of a bulk ingest: the count persisted and the assigned identifiers.</summary>
public sealed record StaObservationBulkResult
{
    /// <summary>Number of observations persisted.</summary>
    [JsonPropertyName("@iot.count")]
    public required long Count { get; init; }

    /// <summary>Assigned identifiers, in input order.</summary>
    [JsonPropertyName("value")]
    public required IReadOnlyList<long> Value { get; init; }
}

/// <summary>
/// Request body for STA Datastream creation (Phase 2 <c>datastream.create</c>). Inline
/// Thing / Sensor / ObservedProperty references are created when their ids do not exist.
/// </summary>
public sealed record StaDatastreamCreate
{
    /// <summary>Datastream name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Datastream description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>The O&amp;M observation type URI.</summary>
    [JsonPropertyName("observationType")]
    public string? ObservationType { get; init; }

    /// <summary>Unit of measurement for the datastream's results.</summary>
    [JsonPropertyName("unitOfMeasurement")]
    public required StaUnitOfMeasurement UnitOfMeasurement { get; init; }

    /// <summary>The related Thing.</summary>
    [JsonPropertyName("Thing")]
    public required StaEntityReference Thing { get; init; }

    /// <summary>The related Sensor.</summary>
    [JsonPropertyName("Sensor")]
    public required StaEntityReference Sensor { get; init; }

    /// <summary>The related ObservedProperty.</summary>
    [JsonPropertyName("ObservedProperty")]
    public required StaEntityReference ObservedProperty { get; init; }
}

/// <summary>STA v1.1 <c>Observation</c> entity DTO.</summary>
public sealed record StaObservation
{
    /// <summary>Entity identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    /// <summary>Canonical self link (<c>@iot.selfLink</c>).</summary>
    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    /// <summary>The time the phenomenon occurred (ISO-8601).</summary>
    [JsonPropertyName("phenomenonTime")]
    public required string PhenomenonTime { get; init; }

    /// <summary>The time the result was generated (ISO-8601), when distinct.</summary>
    [JsonPropertyName("resultTime")]
    public string? ResultTime { get; init; }

    /// <summary>The numeric measurement result.</summary>
    [JsonPropertyName("result")]
    public required double Result { get; init; }

    /// <summary>Navigation link to the owning Datastream.</summary>
    [JsonPropertyName("Datastream@iot.navigationLink")]
    public required string DatastreamNavigationLink { get; init; }

    /// <summary>Navigation link to the related FeatureOfInterest.</summary>
    [JsonPropertyName("FeatureOfInterest@iot.navigationLink")]
    public required string FeatureOfInterestNavigationLink { get; init; }
}
