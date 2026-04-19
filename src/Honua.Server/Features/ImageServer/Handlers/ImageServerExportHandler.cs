// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server export image operations.
/// Provides raster image export with clipping, reprojection, and format conversion.
/// </summary>
internal sealed class ImageServerExportHandler
{
    private const string InlineImageFormat = "image";
    private const int MinOutputDimension = 1;
    private const int MaxAllowedOutputDimension = 4096;
    private const int DefaultMaxOutputDimension = 1024;
    private const int MinCompressionQuality = 1;
    private const int MaxCompressionQuality = 100;
    private const int MinCompression = 0;
    private const int MaxCompression = 100;
    private const int MinPixelType = 0;
    private const int MaxPixelType = 255;

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ITemporaryFileService _temporaryFileService;
    private readonly ILogger<ImageServerExportHandler> _logger;

    public ImageServerExportHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ITemporaryFileService temporaryFileService,
        ILogger<ImageServerExportHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _temporaryFileService = temporaryFileService ?? throw new ArgumentNullException(nameof(temporaryFileService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Exports a rendered image from raster data.
    /// </summary>
    public async Task<IResult> ExportImageAsync(
        HttpContext context,
        int layerId,
        ExportImageRequest request,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "export",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "export-image");

        try
        {
            // Validate layer exists and access
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // Resolve the primary raster without scanning the entire layer.
            var primaryRaster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);
            if (primaryRaster is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }
            var primaryRasterInfo = primaryRaster.Value;

            // Parse export parameters
            var query = ParseExportParameters(request);
            if (query == null)
            {
                ImageServerLog.InvalidExportParameters(_logger, layerId, "Unable to parse export parameters");
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid export parameters");
            }

            // Determine output dimensions and propagate to query
            var coordinateTransformService = context.RequestServices.GetService<ICoordinateTransformService>();
            var (width, height) = await CalculateOutputDimensionsAsync(
                request,
                primaryRasterInfo,
                coordinateTransformService,
                cancellationToken).ConfigureAwait(false);
            var exportQuery = query.Value with { OutputWidth = width, OutputHeight = height };
            var formatName = exportQuery.OutputFormat.ToString();
            ImageServerLog.ExportImageStarted(_logger, layerId, width, height, formatName);

            // Export the image
            var result = await _rasterStore.ExportImageAsync(layerId, primaryRasterInfo.Id, exportQuery, cancellationToken);

            if (WantsInlineImageResponse(request.F))
            {
                ImageServerLog.ExportImageCompleted(_logger, layerId, result.Data.Length);
                scope.SetSuccess(1);
                return Results.File(result.Data, result.ContentType);
            }

            // Store the image temporarily and get the public URL
            var imageUrl = await _temporaryFileService.StoreTemporaryFileAsync(
                result.Data,
                result.ContentType,
                TimeSpan.FromHours(1),
                principal: context.User,
                cancellationToken: cancellationToken);

            var extent = result.Extent;
            extent ??= await _rasterStore.GetExtentAsync(layerId, primaryRasterInfo.Id, cancellationToken);

            var exportResponse = new ExportImageResponse
            {
                Href = imageUrl,
                Width = result.Width,
                Height = result.Height,
                Extent = new ImageServerExtent
                {
                    XMin = extent?.XMin ?? 0,
                    YMin = extent?.YMin ?? 0,
                    XMax = extent?.XMax ?? 1,
                    YMax = extent?.YMax ?? 1,
                    SpatialReference = new SpatialReference
                    {
                        Wkid = extent?.Srid ?? result.Srid ?? 4326,
                        LatestWkid = extent?.Srid ?? result.Srid ?? 4326
                    }
                }
            };

            ImageServerLog.ExportImageCompleted(_logger, layerId, result.Data.Length);
            scope.SetSuccess(1);

            return Results.Json(exportResponse, ImageServerJsonContext.Default.ExportImageResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TemporaryStorageLimitExceededException ex)
        {
            ImageServerLog.ExportStorageLimitReached(_logger, layerId, ex.Message);
            return StandardErrorHelpers.CreateServiceUnavailable(
                context,
                "Temporary export storage is currently at capacity. Please retry shortly.",
                ex.RetryAfterSeconds);
        }
        catch (Exception ex)
        {
            ImageServerLog.ExportImageFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while exporting the image.");
        }
    }

    private static RasterQuery? ParseExportParameters(ExportImageRequest request)
    {
        try
        {
            // renderingRule and mosaicRule are not yet wired into the raster pipeline.
            // Accepting them silently produces unrendered/un-mosaicked imagery that
            // looks wrong to the client without any indication why. Reject explicitly
            // so callers know to drop the parameter until the raster function runtime
            // is available.
            if (!string.IsNullOrWhiteSpace(request.RenderingRule)
                || !string.IsNullOrWhiteSpace(request.MosaicRule))
            {
                return null;
            }

            if (!TryParseRequestedSize(request.Size, out var requestedWidth, out var requestedHeight))
            {
                return null;
            }

            if (request.CompressionQuality.HasValue &&
                (request.CompressionQuality.Value < MinCompressionQuality || request.CompressionQuality.Value > MaxCompressionQuality))
            {
                return null;
            }

            if (request.Compression.HasValue &&
                (request.Compression.Value < MinCompression || request.Compression.Value > MaxCompression))
            {
                return null;
            }

            if (request.PixelType.HasValue &&
                (request.PixelType.Value < MinPixelType || request.PixelType.Value > MaxPixelType))
            {
                return null;
            }

            var outputSrid = SpatialReferenceHelpers.TryParseSrid(request.ImageSr);
            if (!string.IsNullOrWhiteSpace(request.ImageSr) && !outputSrid.HasValue)
            {
                return null;
            }

            var bboxSrid = SpatialReferenceHelpers.TryParseSrid(request.BboxSr);
            if (!string.IsNullOrWhiteSpace(request.BboxSr) && !bboxSrid.HasValue)
            {
                return null;
            }

            if (!RasterParsingHelpers.TryParseRasterFormat(request.Format ?? "png", out var outputFormat))
            {
                return null;
            }

            var query = new RasterQuery
            {
                OutputFormat = outputFormat,
                Quality = request.CompressionQuality,
                OutputSrid = outputSrid
            };

            // Parse bounding box if provided
            if (!string.IsNullOrEmpty(request.Bbox))
            {
                if (!RasterParsingHelpers.TryParseBoundingBox(request.Bbox, out var minX, out var minY, out var maxX, out var maxY))
                {
                    return null; // Invalid bbox format
                }

                var envelope = new Envelope(minX, maxX, minY, maxY);
                var factory = new GeometryFactory();
                var geometry = factory.ToGeometry(envelope);

                var writer = new NetTopologySuite.IO.WKBWriter();
                var geometryBytes = writer.Write(geometry);

                query = query with
                {
                    ClipRegion = new RasterClipRegion
                    {
                        Geometry = geometryBytes,
                        Srid = bboxSrid
                    }
                };
            }

            // Parse resampling method
            if (!string.IsNullOrEmpty(request.Interpolation))
            {
                query = query with { ResamplingAlgorithm = ParseInterpolation(request.Interpolation) };
            }

            return query;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Parses Esri /exportImage size values: either "W" (width only, height inferred
    // from the raster's aspect ratio) or "W,H" (explicit pair). Returns false when
    // either component is outside the permitted output range.
    private static bool TryParseRequestedSize(string? size, out int? width, out int? height)
    {
        width = null;
        height = null;

        if (string.IsNullOrWhiteSpace(size))
        {
            return true;
        }

        var parts = size.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not 1 and not 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            || w < MinOutputDimension || w > MaxAllowedOutputDimension)
        {
            return false;
        }

        width = w;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                || h < MinOutputDimension || h > MaxAllowedOutputDimension)
            {
                return false;
            }
            height = h;
        }

        return true;
    }

    private static async Task<(int width, int height)> CalculateOutputDimensionsAsync(
        ExportImageRequest request,
        Core.Features.Raster.Domain.RasterInfo raster,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var aspectRatio = await GetOutputAspectRatioAsync(request, raster, coordinateTransformService, cancellationToken).ConfigureAwait(false);

        if (!TryParseRequestedSize(request.Size, out var width, out var height))
        {
            // Input is already filtered by ParseExportParameters; fall through to defaults.
            width = null;
            height = null;
        }

        if (width.HasValue && height.HasValue)
        {
            // Both dimensions supplied — honor the client's exact request.
            return (width.Value, height.Value);
        }

        if (width.HasValue)
        {
            return ScaleWidthToAspectRatio(width.Value, aspectRatio, MaxAllowedOutputDimension);
        }

        var sourceWidth = raster.Width > 0 ? raster.Width : DefaultMaxOutputDimension;
        var defaultWidth = Math.Clamp(sourceWidth, MinOutputDimension, DefaultMaxOutputDimension);
        return ScaleWidthToAspectRatio(defaultWidth, aspectRatio, DefaultMaxOutputDimension);
    }

    private static ResamplingAlgorithm ParseInterpolation(string interpolation)
    {
        return interpolation switch
        {
            "RSP_NearestNeighbor" => ResamplingAlgorithm.NearestNeighbor,
            "RSP_BilinearInterpolation" => ResamplingAlgorithm.Bilinear,
            "RSP_CubicConvolution" => ResamplingAlgorithm.Bicubic,
            _ => ResamplingAlgorithm.Bilinear
        };
    }

    private static bool WantsInlineImageResponse(string? format)
        => string.Equals(format, InlineImageFormat, StringComparison.OrdinalIgnoreCase);

    private static async Task<double> GetOutputAspectRatioAsync(
        ExportImageRequest request,
        RasterInfo raster,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        if (await TryGetRequestedExtentAspectRatioAsync(request, raster, coordinateTransformService, cancellationToken).ConfigureAwait(false) is { } bboxAspectRatio)
        {
            return bboxAspectRatio;
        }

        if (raster.Width > 0 && raster.Height > 0)
        {
            return (double)raster.Height / raster.Width;
        }

        return 1d;
    }

    private static async Task<double?> TryGetRequestedExtentAspectRatioAsync(
        ExportImageRequest request,
        RasterInfo raster,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Bbox))
        {
            return null;
        }

        if (!RasterParsingHelpers.TryParseBoundingBox(request.Bbox, out var minX, out var minY, out var maxX, out var maxY))
        {
            return null;
        }

        var bboxSrid = SpatialReferenceHelpers.TryParseSrid(request.BboxSr);
        var targetSrid = SpatialReferenceHelpers.TryParseSrid(request.ImageSr) ?? raster.Srid ?? bboxSrid;
        if (bboxSrid.HasValue && targetSrid.HasValue && bboxSrid.Value != targetSrid.Value)
        {
            if (coordinateTransformService != null)
            {
                var transformed = await coordinateTransformService.TransformExtentAsync(
                    minX,
                    minY,
                    maxX,
                    maxY,
                    bboxSrid.Value,
                    targetSrid.Value,
                    cancellationToken).ConfigureAwait(false);
                if (transformed.HasValue)
                {
                    minX = transformed.Value.MinX;
                    minY = transformed.Value.MinY;
                    maxX = transformed.Value.MaxX;
                    maxY = transformed.Value.MaxY;
                }
            }
            else
            {
                try
                {
                    var transformedExtent = CoordinateTransformer.TransformExtent(
                        new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY),
                        bboxSrid.Value,
                        targetSrid.Value);

                    minX = transformedExtent.MinX;
                    minY = transformedExtent.MinY;
                    maxX = transformedExtent.MaxX;
                    maxY = transformedExtent.MaxY;
                }
                catch (NotSupportedException)
                {
                    // Fall back to the raw bbox aspect ratio when reprojection is unavailable.
                }
            }
        }

        var width = Math.Abs(maxX - minX);
        var height = Math.Abs(maxY - minY);
        if (width <= double.Epsilon || height <= double.Epsilon)
        {
            return null;
        }

        var ratio = height / width;
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            return null;
        }

        return ratio;
    }

    private static (int width, int height) ScaleWidthToAspectRatio(int requestedWidth, double aspectRatio, int maxDimension)
    {
        var width = Math.Clamp(requestedWidth, MinOutputDimension, maxDimension);
        var height = Math.Max(
            MinOutputDimension,
            (int)Math.Round(width * aspectRatio, MidpointRounding.AwayFromZero));

        if (height > maxDimension)
        {
            height = maxDimension;
            width = Math.Max(
                MinOutputDimension,
                (int)Math.Round(height / aspectRatio, MidpointRounding.AwayFromZero));
        }

        return (Math.Clamp(width, MinOutputDimension, maxDimension), Math.Clamp(height, MinOutputDimension, maxDimension));
    }

}
