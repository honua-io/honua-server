// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.NlQuery.Domain;

/// <summary>
/// A constrained filter plan produced by an NL query provider.
/// Maps to a structured JSON schema that LLMs are mechanically constrained to produce.
/// </summary>
public sealed class FilterPlan
{
    /// <summary>
    /// Top-level logical combinator for the clauses.
    /// </summary>
    [JsonPropertyName("combinator")]
    public FilterPlanCombinator Combinator { get; set; }

    /// <summary>
    /// The filter clauses to combine.
    /// </summary>
    [JsonPropertyName("clauses")]
    public FilterPlanClause[] Clauses { get; set; } = [];
}

/// <summary>
/// Logical combinator for filter plan clauses.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FilterPlanCombinator>))]
public enum FilterPlanCombinator
{
    /// <summary>Logical AND — all clauses must match.</summary>
    [JsonStringEnumMemberName("and")]
    And,

    /// <summary>Logical OR — any clause may match.</summary>
    [JsonStringEnumMemberName("or")]
    Or
}

/// <summary>
/// A single clause in a filter plan. Exactly one of the clause-type properties must be set.
/// </summary>
public sealed class FilterPlanClause
{
    /// <summary>
    /// The type discriminator for this clause.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Comparison clause data. Set when <see cref="Type"/> is "comparison".
    /// </summary>
    [JsonPropertyName("comparison")]
    public ComparisonClause? Comparison { get; set; }

    /// <summary>
    /// Spatial clause data. Set when <see cref="Type"/> is "spatial".
    /// </summary>
    [JsonPropertyName("spatial")]
    public SpatialClause? Spatial { get; set; }

    /// <summary>
    /// Temporal clause data. Set when <see cref="Type"/> is "temporal".
    /// </summary>
    [JsonPropertyName("temporal")]
    public TemporalClause? Temporal { get; set; }

    /// <summary>
    /// Nested clause data. Set when <see cref="Type"/> is "nested".
    /// </summary>
    [JsonPropertyName("nested")]
    public NestedClause? Nested { get; set; }
}

/// <summary>
/// A property comparison clause (e.g., population > 50000).
/// </summary>
public sealed class ComparisonClause
{
    /// <summary>
    /// The property/field name to compare.
    /// </summary>
    [JsonPropertyName("property")]
    public string Property { get; set; } = string.Empty;

    /// <summary>
    /// The comparison operator.
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// The value to compare against. May be a string, number, boolean, or array of strings (for "in").
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

/// <summary>
/// A spatial predicate clause (e.g., intersects a polygon, within 5km of a point).
/// </summary>
public sealed class SpatialClause
{
    /// <summary>
    /// The spatial operator: intersects, within, contains, dwithin.
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// GeoJSON geometry object for the spatial predicate.
    /// </summary>
    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; set; }

    /// <summary>
    /// Distance value (required when operator is "dwithin").
    /// </summary>
    [JsonPropertyName("distance")]
    public double? Distance { get; set; }

    /// <summary>
    /// Distance unit (required when operator is "dwithin"). Default: meters.
    /// </summary>
    [JsonPropertyName("distanceUnit")]
    public string? DistanceUnit { get; set; }
}

/// <summary>
/// A temporal predicate clause (e.g., created after 2025-01-01).
/// </summary>
public sealed class TemporalClause
{
    /// <summary>
    /// The property/field name containing the temporal value.
    /// </summary>
    [JsonPropertyName("property")]
    public string Property { get; set; } = string.Empty;

    /// <summary>
    /// The temporal operator: before, after, during.
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// Start of the temporal range (ISO 8601). Required for "after" and "during".
    /// </summary>
    [JsonPropertyName("start")]
    public string? Start { get; set; }

    /// <summary>
    /// End of the temporal range (ISO 8601). Required for "before" and "during".
    /// </summary>
    [JsonPropertyName("end")]
    public string? End { get; set; }
}

/// <summary>
/// A nested clause allowing one level of sub-grouping.
/// </summary>
public sealed class NestedClause
{
    /// <summary>
    /// The logical combinator for the nested clauses.
    /// </summary>
    [JsonPropertyName("combinator")]
    public FilterPlanCombinator Combinator { get; set; }

    /// <summary>
    /// The nested filter clauses.
    /// </summary>
    [JsonPropertyName("clauses")]
    public FilterPlanClause[] Clauses { get; set; } = [];
}
