// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.Ogc.Classic.Wms;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Classic;

/// <summary>
/// AOT-compatible JSON serialization context for classic OGC response models.
/// Includes the shared OGC GeoJSON wire models so WFS 2.0 GeoJSON output is
/// serialized by this protocol-local context rather than the OGC API context.
/// </summary>
[JsonSerializable(typeof(WmsFeatureInfoResponse))]
[JsonSerializable(typeof(WmsFeatureInfoFeature))]
[JsonSerializable(typeof(WmsFeatureInfoFeature[]))]
[JsonSerializable(typeof(FeatureCollection))]
[JsonSerializable(typeof(GeoJsonFeature))]
[JsonSerializable(typeof(GeoJsonFeature[]))]
[JsonSerializable(typeof(SimpleGeoJsonGeometry))]
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
