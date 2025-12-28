// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.OgcFeatures.Models;

namespace Honua.Server.Features.OgcFeatures;

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
[JsonSerializable(typeof(FeatureCollection))]
[JsonSerializable(typeof(GeoJsonFeature))]
[JsonSerializable(typeof(GeoJsonFeature[]))]
[JsonSerializable(typeof(SimpleGeoJsonGeometry))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(QueryablesSchema))]
[JsonSerializable(typeof(JsonSchemaProperty))]
[JsonSerializable(typeof(ImmutableDictionary<string, JsonSchemaProperty>))]
internal sealed partial class OgcJsonContext : JsonSerializerContext
{
}
