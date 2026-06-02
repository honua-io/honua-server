// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Api.Styles.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Styles;

/// <summary>
/// JSON serialization context for OGC API - Styles models.
/// Enables AOT-compatible, source-generated JSON serialization for the Styles slice.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LandingPage))]
[JsonSerializable(typeof(ConformanceDeclaration))]
[JsonSerializable(typeof(Link))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(OgcStylesConformance))]
[JsonSerializable(typeof(StylesList))]
[JsonSerializable(typeof(StyleEntry))]
[JsonSerializable(typeof(ImmutableArray<StyleEntry>))]
[JsonSerializable(typeof(StyleMetadataResponse))]
internal sealed partial class OgcStylesJsonContext : JsonSerializerContext
{
}
