// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Tiles.Models;

namespace Honua.Protocols.Ogc.Api.Tiles;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LandingPage))]
[JsonSerializable(typeof(ConformanceDeclaration))]
[JsonSerializable(typeof(Collections))]
[JsonSerializable(typeof(CollectionInfo))]
[JsonSerializable(typeof(Link))]
[JsonSerializable(typeof(Extent))]
[JsonSerializable(typeof(SpatialExtent))]
[JsonSerializable(typeof(TemporalExtent))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<CollectionInfo>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<double>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<double>>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<string?>>))]
[JsonSerializable(typeof(TileSetsList))]
[JsonSerializable(typeof(TileSetItem))]
[JsonSerializable(typeof(TileSet))]
[JsonSerializable(typeof(TileMatrixSetLimit))]
[JsonSerializable(typeof(TileMatrixSetsList))]
[JsonSerializable(typeof(TileMatrixSetItem))]
[JsonSerializable(typeof(TileMatrixSetDefinition))]
[JsonSerializable(typeof(TileMatrix))]
[JsonSerializable(typeof(ImmutableArray<TileSetItem>))]
[JsonSerializable(typeof(ImmutableArray<TileMatrixSetLimit>))]
[JsonSerializable(typeof(ImmutableArray<TileMatrixSetItem>))]
[JsonSerializable(typeof(ImmutableArray<TileMatrix>))]
internal sealed partial class OgcTilesJsonContext : JsonSerializerContext
{
}
