// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

/// <summary>
/// JSON serialization context for Image Server models.
/// Enables AOT-compatible JSON serialization for Image Server endpoints.
/// </summary>
// Esri omits null-valued fields from ImageServer documents (descriptor /
// conf.json). The ArcGIS Maps SDK for .NET native runtime reads these with a
// strict parser that rejects nulls where it expects a string/array/object, so
// match Esri and drop nulls on the wire (#1456).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ImageServerServiceInfo))]
[JsonSerializable(typeof(ImageServerTimeInfo))]
[JsonSerializable(typeof(ImageServerTimeReference))]
[JsonSerializable(typeof(ImageServerMultidimensionalInfo))]
[JsonSerializable(typeof(ImageServerMultidimensionalVariable))]
[JsonSerializable(typeof(ImageServerMultidimensionalDimension))]
[JsonSerializable(typeof(MultidimensionalInfoResponse))]
[JsonSerializable(typeof(ImageServerSlice))]
[JsonSerializable(typeof(ImageServerSliceDimension))]
[JsonSerializable(typeof(SlicesResponse))]
[JsonSerializable(typeof(ExportImageResponse))]
[JsonSerializable(typeof(IdentifyResponse))]
[JsonSerializable(typeof(ExportImageRequest))]
[JsonSerializable(typeof(IdentifyRequest))]
[JsonSerializable(typeof(SpatialReference))]
[JsonSerializable(typeof(ImageServerExtent))]
[JsonSerializable(typeof(ImageServerStorageInfo))]
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
[JsonSerializable(typeof(ComputeCacheInfoResponse))]
[JsonSerializable(typeof(ImageServerComputedCacheInfo))]
[JsonSerializable(typeof(ComputePixelLocationResponse))]
[JsonSerializable(typeof(PixelLocationPoint))]
[JsonSerializable(typeof(QueryBoundaryResponse))]
[JsonSerializable(typeof(ImageServerProjectResponse))]
[JsonSerializable(typeof(ImageServerExportTilesEstimateResponse))]
[JsonSerializable(typeof(ImageServerExportTilesResponse))]
[JsonSerializable(typeof(ImageServerExportTilesFileInfo))]
[JsonSerializable(typeof(ImageServerExportTilesFileInfo[]))]
[JsonSerializable(typeof(ImageServerExportTilesResults))]
[JsonSerializable(typeof(ImageServerExportTilesResultValue))]
[JsonSerializable(typeof(ImageServerFindResponse))]
[JsonSerializable(typeof(ImageServerFindImage))]
[JsonSerializable(typeof(ImageServerFindPoint))]
[JsonSerializable(typeof(GetSamplesResponse))]
[JsonSerializable(typeof(SampleEntry))]
[JsonSerializable(typeof(SampleLocation))]
[JsonSerializable(typeof(KeyPropertiesResponse))]
[JsonSerializable(typeof(BandProperty))]
[JsonSerializable(typeof(LegendResponse))]
[JsonSerializable(typeof(LegendLayer))]
[JsonSerializable(typeof(LegendEntry))]
[JsonSerializable(typeof(StatisticsResourceResponse))]
[JsonSerializable(typeof(StatisticsEntry))]
[JsonSerializable(typeof(HistogramsResourceResponse))]
[JsonSerializable(typeof(RasterFunctionInfosResponse))]
[JsonSerializable(typeof(RasterFunctionInfoEntry))]
[JsonSerializable(typeof(RasterAttributeTableResponse))]
[JsonSerializable(typeof(RasterAttributeTableField))]
[JsonSerializable(typeof(RasterAttributeTableFeature))]
[JsonSerializable(typeof(AnalyzeResponse))]
[JsonSerializable(typeof(RasterFunctionDocument))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
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
