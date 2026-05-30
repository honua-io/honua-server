// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Api.Maps;

/// <summary>
/// Structured logging for OGC API - Maps operations.
/// Event ID range: 5900-5999 (avoiding collisions with other features).
/// </summary>
internal static partial class OgcMapsLog
{
    [LoggerMessage(
        EventId = 5900,
        Level = LogLevel.Debug,
        Message = "Collection {LayerId} not found")]
    public static partial void CollectionNotFound(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 5901,
        Level = LogLevel.Warning,
        Message = "Invalid map parameters for layer {LayerId}: {ValidationErrors}")]
    public static partial void InvalidMapParameters(ILogger logger, int layerId, string validationErrors);

    [LoggerMessage(
        EventId = 5902,
        Level = LogLevel.Information,
        Message = "Started collection map render for layer {LayerId}: {Width}x{Height}")]
    public static partial void CollectionMapRenderStarted(ILogger logger, int layerId, int width, int height);

    [LoggerMessage(
        EventId = 5903,
        Level = LogLevel.Information,
        Message = "Completed collection map render for layer {LayerId}: {DataSize} bytes")]
    public static partial void CollectionMapRenderCompleted(ILogger logger, int layerId, int dataSize);

    [LoggerMessage(
        EventId = 5904,
        Level = LogLevel.Error,
        Message = "Failed to render collection map for layer {LayerId}")]
    public static partial void CollectionMapRenderFailed(ILogger logger, Exception ex, int layerId);

    [LoggerMessage(
        EventId = 5905,
        Level = LogLevel.Information,
        Message = "Started dataset map render for {LayerCount} layers: {Width}x{Height}")]
    public static partial void DatasetMapRenderStarted(ILogger logger, int layerCount, int width, int height);

    [LoggerMessage(
        EventId = 5906,
        Level = LogLevel.Information,
        Message = "Completed dataset map render for {LayerCount} layers: {DataSize} bytes")]
    public static partial void DatasetMapRenderCompleted(ILogger logger, int layerCount, int dataSize);

    [LoggerMessage(
        EventId = 5907,
        Level = LogLevel.Error,
        Message = "Failed to render dataset map for {LayerCount} layers")]
    public static partial void DatasetMapRenderFailed(ILogger logger, Exception ex, int layerCount);

    [LoggerMessage(
        EventId = 5908,
        Level = LogLevel.Information,
        Message = "Started styled map render for layer {LayerId}, style {StyleId}: {Width}x{Height}")]
    public static partial void StyledMapRenderStarted(ILogger logger, int layerId, string styleId, int width, int height);

    [LoggerMessage(
        EventId = 5909,
        Level = LogLevel.Information,
        Message = "Completed styled map render for layer {LayerId}, style {StyleId}: {DataSize} bytes")]
    public static partial void StyledMapRenderCompleted(ILogger logger, int layerId, string styleId, int dataSize);

    [LoggerMessage(
        EventId = 5910,
        Level = LogLevel.Error,
        Message = "Failed to render styled map for layer {LayerId}, style {StyleId}")]
    public static partial void StyledMapRenderFailed(ILogger logger, Exception ex, int layerId, string styleId);

    [LoggerMessage(
        EventId = 5911,
        Level = LogLevel.Debug,
        Message = "Retrieved {Count} tile sets for layer {LayerId}")]
    public static partial void TileSetsRetrieved(ILogger logger, int layerId, int count);

    [LoggerMessage(
        EventId = 5912,
        Level = LogLevel.Error,
        Message = "Failed to retrieve tile sets for layer {LayerId}")]
    public static partial void TileSetRetrievalFailed(ILogger logger, Exception ex, int layerId);

    [LoggerMessage(
        EventId = 5913,
        Level = LogLevel.Information,
        Message = "OGC Maps conformance requested")]
    public static partial void ConformanceRequested(ILogger logger);

    [LoggerMessage(
        EventId = 5914,
        Level = LogLevel.Warning,
        Message = "Unsupported CRS requested: {Crs}.")]
    public static partial void UnsupportedCrs(ILogger logger, string crs);

    [LoggerMessage(
        EventId = 5915,
        Level = LogLevel.Warning,
        Message = "Map request exceeds maximum dimensions: {RequestedWidth}x{RequestedHeight} (max: {MaxWidth}x{MaxHeight})")]
    public static partial void MapDimensionsExceeded(ILogger logger, int requestedWidth, int requestedHeight, int maxWidth, int maxHeight);

    [LoggerMessage(
        EventId = 5916,
        Level = LogLevel.Debug,
        Message = "Using default bounding box for layer {LayerId}: [{XMin}, {YMin}, {XMax}, {YMax}]")]
    public static partial void UsingDefaultBounds(ILogger logger, int layerId, double xMin, double yMin, double xMax, double yMax);

    [LoggerMessage(
        EventId = 5917,
        Level = LogLevel.Debug,
        Message = "No map raster data found for layer {LayerId}")]
    public static partial void NoMapDataFound(ILogger logger, int layerId);

    [LoggerMessage(
        EventId = 5918,
        Level = LogLevel.Debug,
        Message = "No map raster data found for dataset request with {LayerCount} layers")]
    public static partial void NoDatasetMapDataFound(ILogger logger, int layerCount);

}
