// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// JSON serialization context for cache-related types (AOT-compatible).
/// </summary>
[JsonSerializable(typeof(LayerDefinition))]
[JsonSerializable(typeof(LayerDefinition[]))]
[JsonSerializable(typeof(ServiceDefinition))]
[JsonSerializable(typeof(ServiceDefinition[]))]
[JsonSerializable(typeof(FieldDefinition))]
[JsonSerializable(typeof(FieldDefinition[]))]
[JsonSerializable(typeof(Honua.Core.Features.Shared.Models.SpatialReference), TypeInfoPropertyName = "CacheSpatialReference")]
[JsonSerializable(typeof(FeatureExtent))]
[JsonSerializable(typeof(Relationship))]
[JsonSerializable(typeof(Relationship[]))]
[JsonSerializable(typeof(LayerStyleDefinition))]
[JsonSerializable(typeof(CachedExistenceResult))]
[JsonSerializable(typeof(CachedLayerList))]
[JsonSerializable(typeof(CachedServiceList))]
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

/// <summary>
/// Wrapper for cached layer list to support proper serialization.
/// </summary>
internal sealed record CachedLayerList(LayerDefinition[] Layers);

/// <summary>
/// Wrapper for cached service list to support proper serialization.
/// </summary>
internal sealed record CachedServiceList(ServiceDefinition[] Services);

/// <summary>
/// Wrapper for cached existence checks.
/// </summary>
internal sealed record CachedExistenceResult(bool Exists);

/// <summary>
/// Wrapper for tracked cache-key indexes used to avoid full keyspace scans.
/// </summary>
internal sealed record CachedCacheKeyIndex(string[] Keys);
