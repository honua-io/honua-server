// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.ImageServer.Models;

/// <summary>
/// JSON serialization context for Image Server models.
/// Enables AOT-compatible JSON serialization for Image Server endpoints.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageServerServiceInfo))]
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
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double[]))]
internal sealed partial class ImageServerJsonContext : JsonSerializerContext
{
}
