// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Shared.Models;

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
[JsonSerializable(typeof(CompiledMetadataArtifact))]
[JsonSerializable(typeof(CachedExistenceResult))]
[JsonSerializable(typeof(CachedLayerList))]
[JsonSerializable(typeof(CachedServiceList))]
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
