// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.SensorThings.Services;

internal enum StaPropertyType
{
    Integer,

    Number,

    Text,

    Timestamp,
    Json,
}

internal sealed record StaProperty(
    string Name,
    string Column,
    StaPropertyType Type,
    string? JsonMember = null)
{
    public string EmittedMember => JsonMember ?? Name;
}

internal sealed class StaEntitySchema
{
    private readonly Dictionary<string, StaProperty> _properties;
    private readonly HashSet<string> _navigationProperties;

    private StaEntitySchema(
        string entitySet,
        IReadOnlyList<StaProperty> properties,
        IReadOnlyList<string> navigationProperties,
        string defaultOrderBySql)
    {
        EntitySet = entitySet;
        Properties = properties;
        NavigationProperties = navigationProperties;
        DefaultOrderBySql = defaultOrderBySql;
        _properties = properties.ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        _navigationProperties = new HashSet<string>(navigationProperties, StringComparer.OrdinalIgnoreCase);
    }

    public string EntitySet { get; }

    public IReadOnlyList<StaProperty> Properties { get; }

    public IReadOnlyList<string> NavigationProperties { get; }

    public string DefaultOrderBySql { get; }

    public bool TryGetProperty(string name, out StaProperty property)
    {
        name = name.Trim();
        if (_properties.TryGetValue(name, out property!)) return true;
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && _properties.TryGetValue(name[..slash], out var root) && root.Type == StaPropertyType.Json
            && (root.Name != "unitOfMeasurement" || name.EndsWith("/*", StringComparison.Ordinal))
            && name.Split('/').All(part => part.Length > 0))
        {
            var emitted = root.EmittedMember + name[slash..];
            property = new StaProperty(name, root.Column, StaPropertyType.Json, emitted.EndsWith("/*", StringComparison.Ordinal) ? emitted[..^2] : emitted);
            return true;
        }
        return false;
    }

    public bool IsNavigationProperty(string name) => _navigationProperties.Contains(name.Trim());

    public string PropertyList => string.Join(", ", Properties.Select(property => property.Name));

    private static IEnumerable<StaProperty> IdProperties() =>
    [
        new StaProperty("id", "id", StaPropertyType.Integer, JsonMember: "@iot.id"),
        new StaProperty("@iot.id", "id", StaPropertyType.Integer, JsonMember: "@iot.id"),
    ];

    public static StaEntitySchema Things { get; } = new(
        "Things",
        [
            .. IdProperties(),
            new StaProperty("name", "name", StaPropertyType.Text),
            new StaProperty("description", "description", StaPropertyType.Text),
            new StaProperty("properties", "properties", StaPropertyType.Json),
        ],
        ["Datastreams", "Locations", "HistoricalLocations"],
        "id ASC");

    public static StaEntitySchema Sensors { get; } = new(
        "Sensors",
        [
            .. IdProperties(),
            new StaProperty("name", "name", StaPropertyType.Text),
            new StaProperty("description", "description", StaPropertyType.Text),
            new StaProperty("encodingType", "encoding_type", StaPropertyType.Text),
            new StaProperty("metadata", "metadata_json", StaPropertyType.Json),
            new StaProperty("properties", "properties", StaPropertyType.Json),
        ],
        ["Datastreams"],
        "id ASC");

    public static StaEntitySchema ObservedProperties { get; } = new(
        "ObservedProperties",
        [
            .. IdProperties(),
            new StaProperty("name", "name", StaPropertyType.Text),
            new StaProperty("definition", "definition", StaPropertyType.Text),
            new StaProperty("description", "description", StaPropertyType.Text),
            new StaProperty("properties", "properties", StaPropertyType.Json),
        ],
        ["Datastreams"],
        "id ASC");

    // The datastream list query aliases the catalog table as d and left-joins observations,
    // so every column here is qualified to stay unambiguous inside that statement.
    public static StaEntitySchema Datastreams { get; } = new(
        "Datastreams",
        [
            new StaProperty("id", "d.id", StaPropertyType.Integer, JsonMember: "@iot.id"),
            new StaProperty("@iot.id", "d.id", StaPropertyType.Integer, JsonMember: "@iot.id"),
            new StaProperty("name", "d.name", StaPropertyType.Text),
            new StaProperty("description", "d.description", StaPropertyType.Text),
            new StaProperty("observationType", "d.observation_type", StaPropertyType.Text),
            new StaProperty("unitOfMeasurement", "d.unit_name", StaPropertyType.Json),
            new StaProperty("unitOfMeasurement/name", "d.unit_name", StaPropertyType.Text),
            new StaProperty("unitOfMeasurement/symbol", "d.unit_symbol", StaPropertyType.Text),
            new StaProperty("unitOfMeasurement/definition", "d.unit_definition", StaPropertyType.Text),
            new StaProperty("observedArea", "d.observed_area", StaPropertyType.Json),
            new StaProperty("phenomenonTime", "d.phenomenon_time", StaPropertyType.Text),
            new StaProperty("resultTime", "d.result_time", StaPropertyType.Text),
            new StaProperty("properties", "d.properties", StaPropertyType.Json),
        ],
        ["Thing", "Sensor", "ObservedProperty", "Observations"],
        "d.id ASC");

    public static StaEntitySchema Observations { get; } = new(
        "Observations",
        [
            .. IdProperties(),
            new StaProperty("phenomenonTime", "phenomenon_time", StaPropertyType.Timestamp),
            new StaProperty("resultTime", "result_time", StaPropertyType.Timestamp),
            new StaProperty("result", "result_json", StaPropertyType.Json),
            new StaProperty("validTime", "valid_time", StaPropertyType.Text),
            new StaProperty("resultQuality", "result_quality", StaPropertyType.Json),
            new StaProperty("parameters", "parameters", StaPropertyType.Json),
        ],
        ["Datastream", "FeatureOfInterest"],
        "phenomenon_time ASC, id ASC");

    public static StaEntitySchema Locations { get; } = new("Locations",
        [.. IdProperties(), new("name", "name", StaPropertyType.Text), new("description", "description", StaPropertyType.Text), new("encodingType", "encoding_type", StaPropertyType.Text), new("location", "location", StaPropertyType.Json), new("properties", "properties", StaPropertyType.Json)],
        ["Things", "HistoricalLocations"], "id ASC");

    public static StaEntitySchema HistoricalLocations { get; } = new("HistoricalLocations",
        [.. IdProperties(), new("time", "time", StaPropertyType.Timestamp)], ["Thing", "Locations"], "id ASC");

    public static StaEntitySchema FeaturesOfInterest { get; } = new("FeaturesOfInterest",
        [.. IdProperties(), new("name", "name", StaPropertyType.Text), new("description", "description", StaPropertyType.Text), new("encodingType", "encoding_type", StaPropertyType.Text), new("feature", "feature", StaPropertyType.Json), new("properties", "properties", StaPropertyType.Json)],
        ["Observations"], "id ASC");

    public static StaEntitySchema For(string set) => set switch
    {
        "Things" => Things,
        "Locations" => Locations,
        "HistoricalLocations" => HistoricalLocations,
        "Datastreams" => Datastreams,
        "Sensors" => Sensors,
        "ObservedProperties" => ObservedProperties,
        "Observations" => Observations,
        "FeaturesOfInterest" => FeaturesOfInterest,
        _ => throw new ArgumentException("Unknown sensing entity set.", nameof(set))
    };
}
