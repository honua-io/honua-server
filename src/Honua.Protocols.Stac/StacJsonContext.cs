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
[JsonSerializable(typeof(StacProvider))]
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
[JsonSerializable(typeof(ConformanceDeclaration))]
[JsonSerializable(typeof(QueryablesSchema))]
[JsonSerializable(typeof(JsonSchemaProperty))]
[JsonSerializable(typeof(ImmutableDictionary<string, JsonSchemaProperty>))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<double>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<double>>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<string?>>))]
[JsonSerializable(typeof(ImmutableArray<StacItem>))]
[JsonSerializable(typeof(ImmutableArray<StacProvider>))]
[JsonSerializable(typeof(ImmutableArray<StacSortDefinition>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, StacAsset>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
// Storage-bound item filters project feature attributes. Those values keep their
// provider CLR types, and the shipped image has reflection JSON disabled.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeOnly))]
internal sealed partial class StacJsonContext : JsonSerializerContext
{
}
