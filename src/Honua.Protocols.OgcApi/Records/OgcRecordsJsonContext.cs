// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Api.Records.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Records;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LandingPage))]
[JsonSerializable(typeof(ConformanceDeclaration))]
[JsonSerializable(typeof(OgcRecordCollectionsResponse))]
[JsonSerializable(typeof(OgcRecordCollection))]
[JsonSerializable(typeof(OgcRecordFeatureCollection))]
[JsonSerializable(typeof(OgcRecordFeature))]
[JsonSerializable(typeof(OgcRecordProperties))]
[JsonSerializable(typeof(Link))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<int>))]
[JsonSerializable(typeof(ImmutableArray<double>))]
[JsonSerializable(typeof(ImmutableArray<OgcRecordCollection>))]
[JsonSerializable(typeof(ImmutableArray<OgcRecordFeature>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class OgcRecordsJsonContext : JsonSerializerContext
{
}
