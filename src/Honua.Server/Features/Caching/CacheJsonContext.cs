// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Caching;

/// <summary>
/// JSON serialization context for cache-related types (AOT-compatible).
/// </summary>
[JsonSerializable(typeof(LayerDefinition))]
[JsonSerializable(typeof(LayerDefinition[]))]
[JsonSerializable(typeof(ServiceDefinition))]
[JsonSerializable(typeof(ServiceDefinition[]))]
[JsonSerializable(typeof(FieldDefinition))]
[JsonSerializable(typeof(FieldDefinition[]))]
[JsonSerializable(typeof(SpatialReference))]
[JsonSerializable(typeof(FeatureExtent))]
[JsonSerializable(typeof(Relationship))]
[JsonSerializable(typeof(Relationship[]))]
[JsonSerializable(typeof(CachedLayerList))]
[JsonSerializable(typeof(CachedServiceList))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CacheJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Gets the shared JSON serializer options for cache operations.
    /// </summary>
    public static JsonSerializerOptions Options => Default.Options;
}

/// <summary>
/// Wrapper for cached layer list to support proper serialization.
/// </summary>
internal sealed record CachedLayerList(LayerDefinition[] Layers);

/// <summary>
/// Wrapper for cached service list to support proper serialization.
/// </summary>
internal sealed record CachedServiceList(ServiceDefinition[] Services);
