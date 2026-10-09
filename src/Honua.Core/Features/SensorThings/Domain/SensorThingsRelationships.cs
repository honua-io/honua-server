// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.SensorThings.Domain;

/// <summary>The cardinality and target of a sensing navigation property.</summary>
/// <param name="Target">Target entity set.</param>
/// <param name="Many">Whether the navigation is a collection.</param>
public sealed record SensorThingsRelationship(string Target, bool Many);

/// <summary>Canonical relationship metadata shared by storage and protocol adapters.</summary>
public static class SensorThingsRelationships
{
    /// <summary>All eight core sensing entity sets.</summary>
    public static IReadOnlyList<string> EntitySets { get; } =
        ["Things", "Locations", "HistoricalLocations", "Datastreams", "Sensors", "ObservedProperties", "Observations", "FeaturesOfInterest"];

    /// <summary>Gets the navigation properties of one entity set.</summary>
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
