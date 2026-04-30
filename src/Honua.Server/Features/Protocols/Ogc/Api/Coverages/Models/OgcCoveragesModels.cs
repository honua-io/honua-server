// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Server.Features.Protocols.Ogc.Common;

namespace Honua.Server.Features.Protocols.Ogc.Api.Coverages.Models;

internal sealed record OgcCoverageCollections
{
    [JsonPropertyName("collections")]
    public required ImmutableArray<OgcCoverageCollection> Collections { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

internal sealed record OgcCoverageCollection
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = "coverage";

    [JsonPropertyName("extent")]
    public Extent? Extent { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    [JsonPropertyName("crs")]
    public required ImmutableArray<string> Crs { get; init; }

    [JsonPropertyName("storageCrs")]
    public string? StorageCrs { get; init; }

    [JsonPropertyName("grid")]
    public CoverageGrid? Grid { get; init; }

    [JsonPropertyName("domain")]
    public CoverageDomain? Domain { get; init; }

    [JsonPropertyName("defaultFields")]
    public required ImmutableArray<string> DefaultFields { get; init; }
}

internal sealed record CoverageGrid
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Grid";

    [JsonPropertyName("axisLabels")]
    public required ImmutableArray<string> AxisLabels { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }

    [JsonPropertyName("origin")]
    public ImmutableArray<double>? Origin { get; init; }

    [JsonPropertyName("resolution")]
    public ImmutableArray<double>? Resolution { get; init; }
}

internal sealed record CoverageDomain
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Grid";

    [JsonPropertyName("axes")]
    public required ImmutableDictionary<string, CoverageAxis> Axes { get; init; }
}

internal sealed record CoverageAxis
{
    [JsonPropertyName("start")]
    public required double Start { get; init; }

    [JsonPropertyName("stop")]
    public required double Stop { get; init; }

    [JsonPropertyName("num")]
    public required int Count { get; init; }

    [JsonPropertyName("resolution")]
    public double? Resolution { get; init; }
}

internal sealed record CoverageSchema
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://json-schema.org/draft/2020-12/schema";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("properties")]
    public required ImmutableDictionary<string, CoverageSchemaProperty> Properties { get; init; }
}

internal sealed record CoverageSchemaProperty
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("minimum")]
    public double? Minimum { get; init; }

    [JsonPropertyName("maximum")]
    public double? Maximum { get; init; }

    [JsonPropertyName("x-ogc-propertySeq")]
    public required int PropertySequence { get; init; }

    [JsonPropertyName("x-ogc-variableType")]
    public string VariableType { get; init; } = "continuous";

    [JsonPropertyName("x-honua-pixelType")]
    public string? PixelType { get; init; }

    [JsonPropertyName("x-honua-noDataValue")]
    public double? NoDataValue { get; init; }
}
