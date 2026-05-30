// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Coverages.Models;

/// <summary>
/// JSON serialization context for OGC API - Coverages models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LandingPage))]
[JsonSerializable(typeof(ConformanceDeclaration))]
[JsonSerializable(typeof(Link))]
[JsonSerializable(typeof(Extent))]
[JsonSerializable(typeof(SpatialExtent))]
[JsonSerializable(typeof(TemporalExtent))]
[JsonSerializable(typeof(OgcCoverageCollections))]
[JsonSerializable(typeof(OgcCoverageCollection))]
[JsonSerializable(typeof(CoverageGrid))]
[JsonSerializable(typeof(CoverageDomain))]
[JsonSerializable(typeof(CoverageAxis))]
[JsonSerializable(typeof(CoverageSchema))]
[JsonSerializable(typeof(CoverageSchemaProperty))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<double>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<double>>))]
[JsonSerializable(typeof(ImmutableDictionary<string, CoverageAxis>))]
[JsonSerializable(typeof(ImmutableDictionary<string, CoverageSchemaProperty>))]
internal sealed partial class OgcCoveragesJsonContext : JsonSerializerContext
{
}
