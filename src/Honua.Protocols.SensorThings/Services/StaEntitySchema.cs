// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.SensorThings.Services;

/// <summary>
/// The storage type of a SensorThings entity property. A <c>$filter</c> literal is only
/// bound when it can be converted to this type, so a client typo cannot reach PostgreSQL
/// as an untyped comparison and surface as an unhandled <c>PostgresException</c> (#4203).
/// </summary>
internal enum StaPropertyType
{
    /// <summary>64-bit integer column (<c>bigint</c>).</summary>
    Integer,

    /// <summary>Floating-point column (<c>double precision</c>).</summary>
    Number,

    /// <summary>Text column.</summary>
    Text,

    /// <summary>Timestamp-with-time-zone column.</summary>
    Timestamp,
}

/// <summary>A queryable STA property and the column that backs it.</summary>
/// <param name="Name">The STA property name as it appears on the wire.</param>
/// <param name="Column">The backing column. Never request text: always one of these literals.</param>
/// <param name="Type">The column's storage type, used to validate <c>$filter</c> literals.</param>
/// <param name="JsonMember">
/// The JSON member the property maps to in the entity envelope, when it differs from
/// <paramref name="Name"/> (for example <c>result</c> is emitted as <c>result</c>, but the
/// key property <c>id</c> is emitted as <c>@iot.id</c>).
/// </param>
internal sealed record StaProperty(
    string Name,
    string Column,
    StaPropertyType Type,
    string? JsonMember = null)
{
    /// <summary>The JSON member this property is emitted as.</summary>
    public string EmittedMember => JsonMember ?? Name;
}

/// <summary>
/// The queryable surface of one STA entity set: which properties <c>$filter</c>,
/// <c>$orderby</c> and <c>$select</c> may name, and which navigation properties
/// <c>$expand</c> may name. Anything absent here is rejected rather than ignored — STA 1.1
/// Req 28-35 make the system query options mandatory, and OData 4.0 §8.2.1 requires a
/// service that cannot honour an option to fail the request (#4201).
/// </summary>
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

    /// <summary>The entity-set name, e.g. <c>Things</c>.</summary>
    public string EntitySet { get; }

    /// <summary>Every filterable / orderable / selectable property.</summary>
    public IReadOnlyList<StaProperty> Properties { get; }

    /// <summary>Every recognised STA navigation property, including unavailable relationships that return 501.</summary>
    public IReadOnlyList<string> NavigationProperties { get; }

    /// <summary>The ORDER BY clause used when the request carries no <c>$orderby</c>.</summary>
    public string DefaultOrderBySql { get; }

    /// <summary>Resolves a property by its STA name (case-insensitively).</summary>
    public bool TryGetProperty(string name, out StaProperty property) =>
        _properties.TryGetValue(name.Trim(), out property!);

    /// <summary>True when <paramref name="name"/> is a navigation property of this entity.</summary>
    public bool IsNavigationProperty(string name) => _navigationProperties.Contains(name.Trim());

    /// <summary>Comma-separated property names, for error messages.</summary>
    public string PropertyList => string.Join(", ", Properties.Select(property => property.Name));

    /// <summary>
    /// <c>@iot.id</c> is spelled both ways by real clients (the annotation form and the bare
    /// key name), so both resolve to the id column on every entity set.
    /// </summary>
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
        ],
        ["Datastreams"],
        "id ASC");

    public static StaEntitySchema Sensors { get; } = new(
        "Sensors",
        [
            .. IdProperties(),
            new StaProperty("name", "name", StaPropertyType.Text),
            new StaProperty("description", "description", StaPropertyType.Text),
            new StaProperty("encodingType", "encoding_type", StaPropertyType.Text),
            new StaProperty("metadata", "metadata", StaPropertyType.Text),
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
        ],
        ["Thing", "Sensor", "ObservedProperty", "Observations"],
        "d.id ASC");

    public static StaEntitySchema Observations { get; } = new(
        "Observations",
        [
            .. IdProperties(),
            new StaProperty("phenomenonTime", "phenomenon_time", StaPropertyType.Timestamp),
            new StaProperty("resultTime", "result_time", StaPropertyType.Timestamp),
            new StaProperty("result", "result", StaPropertyType.Number),
        ],
        ["Datastream", "FeatureOfInterest"],
        "phenomenon_time ASC, id ASC");
}
