// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Classic.Wms;

namespace Honua.Protocols.Ogc.Classic;

/// <summary>
/// AOT-compatible JSON serialization context for classic OGC response models.
/// </summary>
/// <remarks>
/// Only <see cref="WmsFeatureInfoResponse"/> is serialized directly, but it transitively
/// carries <see cref="WmsFeatureInfoFeature"/> (and the <c>WmsFeatureInfoFeature[]</c> array)
/// whose <c>Attributes</c> property is a <see cref="Dictionary{TKey,TValue}"/> of
/// <c>string</c> to <c>object?</c>. Those attribute values are polymorphic, so the dictionary
/// plus the <c>object</c> root and every primitive value type that can appear in a feature
/// attribute (string/int/long/double/bool/DateTime/DateTimeOffset, and
/// <see cref="JsonElement"/>/<see cref="IReadOnlyList{T}"/> for nested values) must be
/// explicitly registered. Removing these would make the source-generated serializer fall back
/// to reflection (and fail under trimming/AOT) for those boxed values, so they are
/// intentionally retained.
/// </remarks>
[JsonSerializable(typeof(WmsFeatureInfoResponse))]
[JsonSerializable(typeof(WmsFeatureInfoFeature))]
[JsonSerializable(typeof(WmsFeatureInfoFeature[]))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IReadOnlyList<object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class OgcClassicJsonContext : JsonSerializerContext;
