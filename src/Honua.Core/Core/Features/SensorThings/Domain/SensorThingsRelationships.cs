// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.SensorThings.Domain;

public sealed record SensorThingsRelationship(string Target, bool Many);

public static class SensorThingsRelationships
{
    public const int MaxRelationshipDepth = 10;

    public static IReadOnlyList<string> EntitySets { get; } =
        ["Things", "Locations", "HistoricalLocations", "Datastreams", "Sensors", "ObservedProperties", "Observations", "FeaturesOfInterest"];

    public static IReadOnlyDictionary<string, SensorThingsRelationship> For(string entitySet) => entitySet switch
    {
        "Things" => new Dictionary<string, SensorThingsRelationship> { ["Locations"] = new("Locations", true), ["HistoricalLocations"] = new("HistoricalLocations", true), ["Datastreams"] = new("Datastreams", true) },
        "Locations" => new Dictionary<string, SensorThingsRelationship> { ["Things"] = new("Things", true), ["HistoricalLocations"] = new("HistoricalLocations", true) },
        "HistoricalLocations" => new Dictionary<string, SensorThingsRelationship> { ["Thing"] = new("Things", false), ["Locations"] = new("Locations", true) },
        "Datastreams" => new Dictionary<string, SensorThingsRelationship> { ["Thing"] = new("Things", false), ["Sensor"] = new("Sensors", false), ["ObservedProperty"] = new("ObservedProperties", false), ["Observations"] = new("Observations", true) },
        "Sensors" or "ObservedProperties" => new Dictionary<string, SensorThingsRelationship> { ["Datastreams"] = new("Datastreams", true) },
        "Observations" => new Dictionary<string, SensorThingsRelationship> { ["Datastream"] = new("Datastreams", false), ["FeatureOfInterest"] = new("FeaturesOfInterest", false) },
        "FeaturesOfInterest" => new Dictionary<string, SensorThingsRelationship> { ["Observations"] = new("Observations", true) },
        _ => throw new ArgumentException("Unknown sensing entity set.", nameof(entitySet))
    };
}
