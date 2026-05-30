// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Stac.Models;

namespace Honua.Protocols.Stac;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StacCatalog))]
[JsonSerializable(typeof(StacCollection))]
[JsonSerializable(typeof(StacCollection[]))]
[JsonSerializable(typeof(StacItem))]
[JsonSerializable(typeof(StacItem[]))]
[JsonSerializable(typeof(StacAsset))]
[JsonSerializable(typeof(StacExtent))]
[JsonSerializable(typeof(StacSpatialExtent))]
[JsonSerializable(typeof(StacTemporalExtent))]
[JsonSerializable(typeof(StacItemCollection))]
[JsonSerializable(typeof(StacCollectionsResponse))]
[JsonSerializable(typeof(StacSearchRequest))]
[JsonSerializable(typeof(StacSearchContext))]
[JsonSerializable(typeof(StacFieldsExtension))]
[JsonSerializable(typeof(StacSortDefinition))]
[JsonSerializable(typeof(Link))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<double>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<double>>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<string?>>))]
[JsonSerializable(typeof(ImmutableArray<StacItem>))]
[JsonSerializable(typeof(ImmutableArray<StacSortDefinition>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, StacAsset>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
internal sealed partial class StacJsonContext : JsonSerializerContext
{
}
