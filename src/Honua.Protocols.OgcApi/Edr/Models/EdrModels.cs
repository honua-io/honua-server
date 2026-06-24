// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Edr.Models;

/// <summary>OGC API - EDR landing page.</summary>
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

/// <summary>OGC API - EDR conformance declaration.</summary>
internal sealed record EdrConformance
{
    [JsonPropertyName("conformsTo")]
    public required ImmutableArray<string> ConformsTo { get; init; }
}

/// <summary>The EDR collections document.</summary>
internal sealed record EdrCollections
{
    [JsonPropertyName("collections")]
    public required ImmutableArray<EdrCollection> Collections { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>An EDR collection: a coverage/datacube exposing position and cube queries.</summary>
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

/// <summary>The supported EDR query families for a collection.</summary>
internal sealed record EdrDataQueries
{
    [JsonPropertyName("position")]
    public EdrDataQuery? Position { get; init; }

    [JsonPropertyName("cube")]
    public EdrDataQuery? Cube { get; init; }
}

/// <summary>Metadata for a single EDR query family.</summary>
internal sealed record EdrDataQuery
{
    [JsonPropertyName("link")]
    public required EdrQueryLink Link { get; init; }
}

/// <summary>A link describing an EDR query endpoint.</summary>
internal sealed record EdrQueryLink
{
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    [JsonPropertyName("rel")]
    public string Rel { get; init; } = "data";

    [JsonPropertyName("variables")]
    public EdrQueryVariables? Variables { get; init; }
}

/// <summary>Query-type variables for an EDR data query.</summary>
internal sealed record EdrQueryVariables
{
    [JsonPropertyName("query_type")]
    public required string QueryType { get; init; }

    [JsonPropertyName("output_formats")]
    public required ImmutableArray<string> OutputFormats { get; init; }
}

/// <summary>An EDR parameter (observed property / coverage range field).</summary>
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

/// <summary>An EDR unit-of-measurement object.</summary>
internal sealed record EdrUnit
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }
}

/// <summary>An EDR observed-property descriptor.</summary>
internal sealed record EdrObservedProperty
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }
}

// ---- CoverageJSON response models (RFC-style, used for position + cube) ----

/// <summary>A CoverageJSON Coverage document returned by position/cube queries.</summary>
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

/// <summary>The CoverageJSON domain (axes definition).</summary>
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

/// <summary>A CoverageJSON axis (a list of coordinate values).</summary>
internal sealed record CoverageJsonAxis
{
    [JsonPropertyName("values")]
    public required ImmutableArray<double> Values { get; init; }
}

/// <summary>A CoverageJSON temporal axis (a list of ISO-8601 instants).</summary>
internal sealed record CoverageJsonTimeAxis
{
    [JsonPropertyName("values")]
    public required ImmutableArray<string> Values { get; init; }
}

/// <summary>A CoverageJSON referencing-system entry.</summary>
internal sealed record CoverageJsonReferencing
{
    [JsonPropertyName("coordinates")]
    public required ImmutableArray<string> Coordinates { get; init; }

    [JsonPropertyName("system")]
    public required CoverageJsonReferenceSystem System { get; init; }
}

/// <summary>A CoverageJSON reference system.</summary>
internal sealed record CoverageJsonReferenceSystem
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>A CoverageJSON NdArray range of values for a single parameter.</summary>
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

/// <summary>Source-generated JSON context for EDR + CoverageJSON wire models.</summary>
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
