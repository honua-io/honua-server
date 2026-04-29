// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// JSON serialization context for catalog metadata.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogMetadata))]
[JsonSerializable(typeof(AccessPolicy))]
[JsonSerializable(typeof(LayerTimeInfo))]
[JsonSerializable(typeof(MapServerConfig))]
[JsonSerializable(typeof(RasterMosaicSettings))]
[JsonSerializable(typeof(StacCatalogMetadata))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
