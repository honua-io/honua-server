// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Infrastructure.Caching;

[JsonSerializable(typeof(MetadataV2Field))]
[JsonSerializable(typeof(MetadataV2Field[]))]
[JsonSerializable(typeof(Honua.Core.Features.Shared.Models.SpatialReference), TypeInfoPropertyName = "CacheSpatialReference")]
[JsonSerializable(typeof(FeatureExtent))]
[JsonSerializable(typeof(Relationship))]
[JsonSerializable(typeof(Relationship[]))]
[JsonSerializable(typeof(LayerStyleDefinition))]
[JsonSerializable(typeof(EnrichmentDatasetRecord))]
[JsonSerializable(typeof(EnrichmentDatasetRecord[]))]
[JsonSerializable(typeof(CachedExistenceResult))]
[JsonSerializable(typeof(CachedCacheKeyIndex))]
[JsonSerializable(typeof(CachedResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(byte[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CacheJsonContext : JsonSerializerContext
{
}

internal sealed record CachedExistenceResult(bool Exists);

internal sealed record CachedCacheKeyIndex(string[] Keys, Dictionary<string, long>? ExpiresAtUtcTicks = null);
