// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Edr.Models;

internal sealed record EdrLandingPage
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = "Honua OGC API Environmental Data Retrieval";

    [JsonPropertyName("description")]
    public string Description { get; init; } =
        "Position and cube queries over registered environmental coverages / datacubes.";

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

internal sealed record EdrConformance
{
    [JsonPropertyName("conformsTo")]
    public required ImmutableArray<string> ConformsTo { get; init; }
}

internal sealed record EdrCollections
{
    [JsonPropertyName("collections")]
    public required ImmutableArray<EdrCollection> Collections { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

internal sealed record EdrCollection
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    [JsonPropertyName("extent")]
    public Extent? Extent { get; init; }

    [JsonPropertyName("data_queries")]
    public required EdrDataQueries DataQueries { get; init; }

    [JsonPropertyName("crs")]
    public required ImmutableArray<string> Crs { get; init; }

    [JsonPropertyName("output_formats")]
    public required ImmutableArray<string> OutputFormats { get; init; }

    [JsonPropertyName("parameter_names")]
    public required ImmutableDictionary<string, EdrParameter> ParameterNames { get; init; }
}

internal sealed record EdrDataQueries
{
    [JsonPropertyName("position")]
    public EdrDataQuery? Position { get; init; }

    [JsonPropertyName("cube")]
    public EdrDataQuery? Cube { get; init; }
}

internal sealed record EdrDataQuery
{
    [JsonPropertyName("link")]
    public required EdrQueryLink Link { get; init; }
}

internal sealed record EdrQueryLink
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("rel")]
    public string Rel { get; init; } = "data";

    [JsonPropertyName("variables")]
    public EdrQueryVariables? Variables { get; init; }
}

internal sealed record EdrQueryVariables
{
    [JsonPropertyName("query_type")]
    public required string QueryType { get; init; }

    [JsonPropertyName("output_formats")]
    public required ImmutableArray<string> OutputFormats { get; init; }
}

internal sealed record EdrParameter
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Parameter";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("unit")]
    public EdrUnit? Unit { get; init; }

    [JsonPropertyName("observedProperty")]
    public required EdrObservedProperty ObservedProperty { get; init; }
}

internal sealed record EdrUnit
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }
}

internal sealed record EdrObservedProperty
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }
}

// ---- CoverageJSON response models (RFC-style, used for position + cube) ----

internal sealed record CoverageJson
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Coverage";

    [JsonPropertyName("domain")]
    public required CoverageJsonDomain Domain { get; init; }

    [JsonPropertyName("parameters")]
    public required ImmutableDictionary<string, EdrParameter> Parameters { get; init; }

    [JsonPropertyName("ranges")]
    public required ImmutableDictionary<string, CoverageJsonRange> Ranges { get; init; }
}

internal sealed record CoverageJsonDomain
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Domain";

    [JsonPropertyName("domainType")]
    public required string DomainType { get; init; }

    [JsonPropertyName("axes")]
    public required ImmutableDictionary<string, CoverageJsonAxis> Axes { get; init; }

    [JsonPropertyName("referencing")]
    public required ImmutableArray<CoverageJsonReferencing> Referencing { get; init; }
}

internal sealed record CoverageJsonAxis
{
    [JsonPropertyName("values")]
    public required ImmutableArray<double> Values { get; init; }
}

internal sealed record CoverageJsonTimeAxis
{
    [JsonPropertyName("values")]
    public required ImmutableArray<string> Values { get; init; }
}

internal sealed record CoverageJsonReferencing
{
    [JsonPropertyName("coordinates")]
    public required ImmutableArray<string> Coordinates { get; init; }

    [JsonPropertyName("system")]
    public required CoverageJsonReferenceSystem System { get; init; }
}

internal sealed record CoverageJsonReferenceSystem
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

internal sealed record CoverageJsonRange
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "NdArray";

    [JsonPropertyName("dataType")]
    public string DataType { get; init; } = "float";

    [JsonPropertyName("axisNames")]
    public required ImmutableArray<string> AxisNames { get; init; }

    [JsonPropertyName("shape")]
    public required ImmutableArray<int> Shape { get; init; }

    [JsonPropertyName("values")]
    public required ImmutableArray<double?> Values { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EdrLandingPage))]
[JsonSerializable(typeof(EdrConformance))]
[JsonSerializable(typeof(EdrCollections))]
[JsonSerializable(typeof(EdrCollection))]
[JsonSerializable(typeof(CoverageJson))]
[JsonSerializable(typeof(CoverageJsonTimeAxis))]
internal sealed partial class EdrJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
