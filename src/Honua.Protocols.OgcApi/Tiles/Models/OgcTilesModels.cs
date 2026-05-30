// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Tiles.Models;

/// <summary>
/// Response payload for a tilesets list.
/// </summary>
public sealed record TileSetsList : ICollectionResponse<TileSetItem>
{
    [JsonPropertyName("tilesets")]
    public required ImmutableArray<TileSetItem> Tilesets { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<TileSetItem> ICollectionResponse<TileSetItem>.Items => Tilesets;
    ImmutableArray<ILink>? ICollectionResponse<TileSetItem>.Links => Links?.Cast<ILink>().ToImmutableArray();
    IPaginationMetadata? ICollectionResponse<TileSetItem>.Pagination => null;
}

/// <summary>
/// Summary metadata for a tileset entry.
/// </summary>
public sealed record TileSetItem
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("dataType")]
    public required string DataType { get; init; }

    [JsonPropertyName("crs")]
    public required string Crs { get; init; }

    [JsonPropertyName("tileMatrixSetId")]
    public required string TileMatrixSetId { get; init; }

    [JsonPropertyName("tileMatrixSetURI")]
    public string? TileMatrixSetUri { get; init; }

    [JsonPropertyName("tileMatrixSetLimits")]
    public ImmutableArray<TileMatrixSetLimit>? TileMatrixSetLimits { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Detailed tileset metadata response.
/// </summary>
public sealed record TileSet
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("dataType")]
    public required string DataType { get; init; }

    [JsonPropertyName("crs")]
    public required string Crs { get; init; }

    [JsonPropertyName("tileMatrixSetId")]
    public required string TileMatrixSetId { get; init; }

    [JsonPropertyName("tileMatrixSetURI")]
    public string? TileMatrixSetUri { get; init; }

    [JsonPropertyName("tileMatrixSetLimits")]
    public ImmutableArray<TileMatrixSetLimit>? TileMatrixSetLimits { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }

    [JsonPropertyName("mediaTypes")]
    public ImmutableArray<string>? MediaTypes { get; init; }
}

/// <summary>
/// Limits for a tile matrix within a tileset.
/// </summary>
public sealed record TileMatrixSetLimit
{
    [JsonPropertyName("tileMatrix")]
    public required string TileMatrix { get; init; }

    [JsonPropertyName("minTileRow")]
    public int MinTileRow { get; init; }

    [JsonPropertyName("maxTileRow")]
    public int MaxTileRow { get; init; }

    [JsonPropertyName("minTileCol")]
    public int MinTileCol { get; init; }

    [JsonPropertyName("maxTileCol")]
    public int MaxTileCol { get; init; }
}

/// <summary>
/// Response payload for a tile matrix sets list.
/// </summary>
public sealed record TileMatrixSetsList : ICollectionResponse<TileMatrixSetItem>
{
    [JsonPropertyName("tileMatrixSets")]
    public required ImmutableArray<TileMatrixSetItem> TileMatrixSets { get; init; }

    [JsonPropertyName("links")]
    public ImmutableArray<Link>? Links { get; init; }

    // ICollectionResponse implementation
    ImmutableArray<TileMatrixSetItem> ICollectionResponse<TileMatrixSetItem>.Items => TileMatrixSets;
    ImmutableArray<ILink>? ICollectionResponse<TileMatrixSetItem>.Links => Links?.Cast<ILink>().ToImmutableArray();
    IPaginationMetadata? ICollectionResponse<TileMatrixSetItem>.Pagination => null;
}

/// <summary>
/// Summary metadata for a tile matrix set entry.
/// </summary>
public sealed record TileMatrixSetItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    [JsonPropertyName("links")]
    public required ImmutableArray<Link> Links { get; init; }
}

/// <summary>
/// Full tile matrix set definition response.
/// </summary>
public sealed record TileMatrixSetDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("crs")]
    public required string Crs { get; init; }

    [JsonPropertyName("wellKnownScaleSet")]
    public string? WellKnownScaleSet { get; init; }

    [JsonPropertyName("tileMatrices")]
    public required ImmutableArray<TileMatrix> TileMatrices { get; init; }
}

/// <summary>
/// Tile matrix definition within a tile matrix set.
/// </summary>
public sealed record TileMatrix
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("scaleDenominator")]
    public required double ScaleDenominator { get; init; }

    [JsonPropertyName("cellSize")]
    public required double CellSize { get; init; }

    [JsonPropertyName("pointOfOrigin")]
    public required ImmutableArray<double> PointOfOrigin { get; init; }

    [JsonPropertyName("tileWidth")]
    public int TileWidth { get; init; }

    [JsonPropertyName("tileHeight")]
    public int TileHeight { get; init; }

    [JsonPropertyName("matrixWidth")]
    public int MatrixWidth { get; init; }

    [JsonPropertyName("matrixHeight")]
    public int MatrixHeight { get; init; }

    [JsonPropertyName("cornerOfOrigin")]
    public string? CornerOfOrigin { get; init; }
}
