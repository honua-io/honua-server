// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.SensorThings.Models;

public sealed record StaServiceDocument
{
    [JsonPropertyName("value")]
    public required IReadOnlyList<StaEntitySetLink> Value { get; init; }

    [JsonPropertyName("serverSettings")]
    public required StaServerSettings ServerSettings { get; init; }
}

public sealed record StaEntitySetLink
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

public sealed record StaServerSettings
{
    [JsonPropertyName("conformance")]
    public IReadOnlyList<string> Conformance { get; init; } = [];

    [JsonPropertyName("honua:unresolvedFeatureOfInterestCount")]
    public long UnresolvedFeatureOfInterestCount { get; init; }
}

public sealed record StaEntitySet<T>
{
    [JsonPropertyName("@iot.count")]
    public long? Count { get; init; }

    [JsonPropertyName("value")]
    public required IReadOnlyList<T> Value { get; init; }

    [JsonPropertyName("@iot.nextLink")]
    public string? NextLink { get; init; }
}

public sealed record StaThing
{
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    [JsonPropertyName("Datastreams@iot.navigationLink")]
    public required string DatastreamsNavigationLink { get; init; }
}

public sealed record StaSensor
{
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("encodingType")]
    public required string EncodingType { get; init; }

    [JsonPropertyName("metadata")]
    public required string Metadata { get; init; }

    [JsonPropertyName("Datastreams@iot.navigationLink")]
    public required string DatastreamsNavigationLink { get; init; }
}

public sealed record StaObservedProperty
{
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("definition")]
    public required string Definition { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("Datastreams@iot.navigationLink")]
    public required string DatastreamsNavigationLink { get; init; }
}

public sealed record StaUnitOfMeasurement
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Name { get; init; }

    [JsonPropertyName("symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Symbol { get; init; }

    [JsonPropertyName("definition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? Definition { get; init; }
}

public sealed record StaDatastream
{
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("observationType")]
    public required string ObservationType { get; init; }

    [JsonPropertyName("unitOfMeasurement")]
    public required StaUnitOfMeasurement UnitOfMeasurement { get; init; }

    [JsonPropertyName("phenomenonTime")]
    public string? PhenomenonTime { get; init; }

    [JsonPropertyName("Observations@iot.navigationLink")]
    public required string ObservationsNavigationLink { get; init; }

    [JsonPropertyName("Thing@iot.navigationLink")]
    public required string ThingNavigationLink { get; init; }

    [JsonPropertyName("Sensor@iot.navigationLink")]
    public required string SensorNavigationLink { get; init; }

    [JsonPropertyName("ObservedProperty@iot.navigationLink")]
    public required string ObservedPropertyNavigationLink { get; init; }

    [JsonPropertyName("Observations")]
    public IReadOnlyList<StaObservation>? Observations { get; init; }

    [JsonPropertyName("Thing")]
    public StaThing? Thing { get; init; }

    [JsonPropertyName("Sensor")]
    public StaSensor? Sensor { get; init; }

    [JsonPropertyName("ObservedProperty")]
    public StaObservedProperty? ObservedProperty { get; init; }
}

public sealed record StaObservationCreate
{
    [JsonPropertyName("phenomenonTime")]
    public string? PhenomenonTime { get; init; }

    [JsonPropertyName("resultTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ResultTime { get; init; }

    [JsonPropertyName("result")]
    public required double Result { get; init; }

    [JsonPropertyName("Datastream")]
    public StaEntityReference? Datastream { get; init; }
}

public sealed record StaEntityReference
{
    [JsonPropertyName("@iot.id")]
    public long IotId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record StaObservationBulkCreate
{
    [JsonPropertyName("value")]
    public required IReadOnlyList<StaObservationCreate> Value { get; init; }
}

public sealed record StaObservationBulkResult
{
    [JsonPropertyName("@iot.count")]
    public required long Count { get; init; }

    [JsonPropertyName("value")]
    public required IReadOnlyList<long> Value { get; init; }
}

public sealed record StaDatastreamCreate
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("observationType")]
    public string? ObservationType { get; init; }

    [JsonPropertyName("unitOfMeasurement")]
    public required StaUnitOfMeasurement UnitOfMeasurement { get; init; }

    [JsonPropertyName("Thing")]
    public required StaEntityReference Thing { get; init; }

    [JsonPropertyName("Sensor")]
    public required StaEntityReference Sensor { get; init; }

    [JsonPropertyName("ObservedProperty")]
    public required StaEntityReference ObservedProperty { get; init; }
}

public sealed record StaObservation
{
    [JsonPropertyName("@iot.id")]
    public required long IotId { get; init; }

    [JsonPropertyName("@iot.selfLink")]
    public required string IotSelfLink { get; init; }

    [JsonPropertyName("phenomenonTime")]
    public required string PhenomenonTime { get; init; }

    [JsonPropertyName("resultTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ResultTime { get; init; }

    [JsonPropertyName("result")]
    public required System.Text.Json.JsonElement Result { get; init; }

    [JsonPropertyName("Datastream@iot.navigationLink")]
    public required string DatastreamNavigationLink { get; init; }

}
