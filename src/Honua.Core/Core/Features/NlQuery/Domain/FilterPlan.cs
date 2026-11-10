// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.NlQuery.Domain;

public sealed class FilterPlan
{
    [JsonPropertyName("combinator")]
    public FilterPlanCombinator Combinator { get; set; }

    // A null clauses array from the provider is stored as empty so readers do not throw.
    [JsonPropertyName("clauses")]
    public FilterPlanClause[] Clauses
    {
        get => _clauses;
        set => _clauses = value ?? [];
    }

    private FilterPlanClause[] _clauses = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<FilterPlanCombinator>))]
public enum FilterPlanCombinator
{
    [JsonStringEnumMemberName("and")]
    And,

    [JsonStringEnumMemberName("or")]
    Or
}

public sealed class FilterPlanClause
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("comparison")]
    public ComparisonClause? Comparison { get; set; }

    [JsonPropertyName("spatial")]
    public SpatialClause? Spatial { get; set; }

    [JsonPropertyName("temporal")]
    public TemporalClause? Temporal { get; set; }

    [JsonPropertyName("nested")]
    public NestedClause? Nested { get; set; }
}

public sealed class ComparisonClause
{
    [JsonPropertyName("property")]
    public string Property { get; set; } = string.Empty;

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

public sealed class SpatialClause
{
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; set; }

    [JsonPropertyName("distance")]
    public double? Distance { get; set; }

    [JsonPropertyName("distanceUnit")]
    public string? DistanceUnit { get; set; }
}

public sealed class TemporalClause
{
    [JsonPropertyName("property")]
    public string Property { get; set; } = string.Empty;

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public string? Start { get; set; }

    [JsonPropertyName("end")]
    public string? End { get; set; }
}

public sealed class NestedClause
{
    [JsonPropertyName("combinator")]
    public FilterPlanCombinator Combinator { get; set; }

    [JsonPropertyName("clauses")]
    public FilterPlanClause[] Clauses
    {
        get => _clauses;
        set => _clauses = value ?? [];
    }

    private FilterPlanClause[] _clauses = [];
}
