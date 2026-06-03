// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.MapServer.Models;

/// <summary>
/// AOT-compatible JSON serialization context for MapServer models.
/// </summary>
[JsonSerializable(typeof(MapServerResponse))]
[JsonSerializable(typeof(MapServerDocumentInfo))]
[JsonSerializable(typeof(MapServerLayerInfo))]
[JsonSerializable(typeof(MapServerLayerInfo[]))]
[JsonSerializable(typeof(MapServerTableInfo))]
[JsonSerializable(typeof(MapServerTableInfo[]))]
[JsonSerializable(typeof(MapServerLayerResponse))]
[JsonSerializable(typeof(MapServerFieldInfo))]
[JsonSerializable(typeof(MapServerFieldInfo[]))]
[JsonSerializable(typeof(IdentifyResponse))]
[JsonSerializable(typeof(IdentifyResult))]
[JsonSerializable(typeof(IdentifyResult[]))]
[JsonSerializable(typeof(AllLayersAndTablesResponse))]
[JsonSerializable(typeof(MapServerLayerResponse[]))]
[JsonSerializable(typeof(MapServerQueryDomainsResponse))]
[JsonSerializable(typeof(MapServerDomainInfo))]
[JsonSerializable(typeof(MapServerDomainInfo[]))]
[JsonSerializable(typeof(MapServerDomainCodedValue))]
[JsonSerializable(typeof(MapServerDomainCodedValue[]))]
[JsonSerializable(typeof(FeatureResourceResponse))]
[JsonSerializable(typeof(LegendResponse))]
[JsonSerializable(typeof(LegendLayerInfo))]
[JsonSerializable(typeof(LegendLayerInfo[]))]
[JsonSerializable(typeof(LegendEntry))]
[JsonSerializable(typeof(LegendEntry[]))]
[JsonSerializable(typeof(ExportImageResponse))]
[JsonSerializable(typeof(FindResponse))]
[JsonSerializable(typeof(FindResult))]
[JsonSerializable(typeof(FindResult[]))]
[JsonSerializable(typeof(TileInfo))]
[JsonSerializable(typeof(TileOrigin))]
[JsonSerializable(typeof(LevelOfDetail))]
[JsonSerializable(typeof(LevelOfDetail[]))]
[JsonSerializable(typeof(EsriSpatialReference))]
[JsonSerializable(typeof(EsriExtent))]
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
internal sealed partial class MapServerJsonContext : JsonSerializerContext;
