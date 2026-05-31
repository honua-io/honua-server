// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// JSON serialization context for Image Server models.
/// Enables AOT-compatible JSON serialization for Image Server endpoints.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageServerServiceInfo))]
[JsonSerializable(typeof(ImageServerTimeInfo))]
[JsonSerializable(typeof(ImageServerTimeReference))]
[JsonSerializable(typeof(ImageServerMultidimensionalInfo))]
[JsonSerializable(typeof(ImageServerMultidimensionalVariable))]
[JsonSerializable(typeof(ExportImageResponse))]
[JsonSerializable(typeof(IdentifyResponse))]
[JsonSerializable(typeof(ExportImageRequest))]
[JsonSerializable(typeof(IdentifyRequest))]
[JsonSerializable(typeof(SpatialReference))]
[JsonSerializable(typeof(ImageServerExtent))]
[JsonSerializable(typeof(Field))]
[JsonSerializable(typeof(TileInfo))]
[JsonSerializable(typeof(Point))]
[JsonSerializable(typeof(LevelOfDetail))]
[JsonSerializable(typeof(RasterFunctionInfo))]
[JsonSerializable(typeof(RasterTypeInfo))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(CatalogQueryResponse))]
[JsonSerializable(typeof(CatalogQueryFeature))]
[JsonSerializable(typeof(CatalogQueryGeometry))]
[JsonSerializable(typeof(CatalogObjectIdsResponse))]
[JsonSerializable(typeof(CatalogCountResponse))]
[JsonSerializable(typeof(CatalogExtentResponse))]
[JsonSerializable(typeof(ComputeStatisticsHistogramsResponse))]
[JsonSerializable(typeof(BandStatistic))]
[JsonSerializable(typeof(BandHistogram))]
[JsonSerializable(typeof(ComputeHistogramsResponse))]
[JsonSerializable(typeof(GetSamplesResponse))]
[JsonSerializable(typeof(SampleEntry))]
[JsonSerializable(typeof(SampleLocation))]
[JsonSerializable(typeof(KeyPropertiesResponse))]
[JsonSerializable(typeof(BandProperty))]
[JsonSerializable(typeof(LegendResponse))]
[JsonSerializable(typeof(LegendLayer))]
[JsonSerializable(typeof(LegendEntry))]
[JsonSerializable(typeof(AnalyzeResponse))]
[JsonSerializable(typeof(RasterFunctionDocument))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(double[][]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double?[]))]
internal sealed partial class ImageServerJsonContext : JsonSerializerContext
{
}
