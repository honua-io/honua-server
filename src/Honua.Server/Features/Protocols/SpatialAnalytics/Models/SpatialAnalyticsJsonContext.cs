// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.SpatialAnalytics.Models;

/// <summary>
/// AOT-compatible JSON serialization context for the spatial analytics endpoints.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SpatialAnalyticsFeatureCollection))]
[JsonSerializable(typeof(SpatialAnalyticsFeature))]
[JsonSerializable(typeof(SpatialAnalyticsFeature[]))]
[JsonSerializable(typeof(SpatialAnalyticsMetadata))]
[JsonSerializable(typeof(SpatialAnalyticsGeometry))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
// Primitive types that may appear as values inside the feature's
// Dictionary<string, object?> properties bag (PostgreSQL column values
// flowing through IReadOnlyDictionary<string, object?>).
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(byte[]))]
// Array types produced by spatial-join carry fields (array_agg(text) → string[],
// array_agg(int/bigint) → long[], array_agg(double precision) → double[]) so the
// polymorphic Dictionary<string, object?> serializer can emit them as JSON arrays.
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(bool[]))]
internal sealed partial class SpatialAnalyticsJsonContext : JsonSerializerContext;
