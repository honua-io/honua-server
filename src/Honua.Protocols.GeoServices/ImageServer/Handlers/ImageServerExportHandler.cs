// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server export image operations.
/// Provides raster image export with clipping and format conversion using the
/// public contract that this service currently supports.
/// </summary>
internal sealed class ImageServerExportHandler
{
    private const string InlineImageFormat = "image";
    private const int DefaultOutputDimension = 400;
    private const int MinOutputDimension = 1;
    private const int MaxAllowedOutputDimension = 4096;
    private const int MinCompressionQuality = 0;
    private const int MaxCompressionQuality = 100;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ITemporaryFileService _temporaryFileService;
    private readonly ILogger<ImageServerExportHandler> _logger;

    public ImageServerExportHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ITemporaryFileService temporaryFileService,
        ILogger<ImageServerExportHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
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
            "export-image",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "export-image");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!TryParseExportParameters(request, out var exportQuery, out var parseError))
            {
                ImageServerLog.InvalidExportParameters(_logger, layerId, parseError.Detail);
                return parseError.IsNotImplemented
                    ? StandardErrorHelpers.CreateNotImplemented(context, parseError.Detail)
                    : StandardErrorHelpers.CreateBadRequest(context, parseError.Detail);
            }

            if (!ImageServerMosaicHelpers.TryParseTime(request.Time, out var timestamp, out var timeError))
            {
                ImageServerLog.InvalidExportParameters(_logger, layerId, timeError ?? "Invalid time parameter");
                return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter.");
            }

            var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
            if (editionError != null)
            {
                return editionError;
            }

            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, request.MosaicRule);
            var selectionQuery = new RasterSelectionQuery
            {
                Geometry = exportQuery.ClipRegion?.Geometry,
                GeometrySrid = exportQuery.ClipRegion?.Srid,
                Timestamp = timestamp,
            };

            var selectedRasters = await _rasterStore.QueryRastersAsync(layerId, selectionQuery, cancellationToken);
            if (selectedRasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            var aggregateExtent = ImageServerMosaicHelpers.ComputeAggregateExtent(selectedRasters);

            var outputFormat = exportQuery.OutputFormat.ToString();
            ImageServerLog.ExportImageStarted(
                _logger,
                layerId,
                exportQuery.OutputWidth ?? DefaultOutputDimension,
                exportQuery.OutputHeight ?? DefaultOutputDimension,
                outputFormat);

            var result = selectedRasters.Length == 1
                ? await _rasterStore.ExportImageAsync(layerId, selectedRasters[0].Id, exportQuery, cancellationToken)
                : await _rasterStore.ExportMosaicAsync(
                    layerId,
                    selectedRasters.Select(r => r.Id).ToArray(),
                    mergeStrategy,
                    exportQuery,
                    cancellationToken);

            if (WantsInlineImageResponse(request.F))
            {
                ImageServerLog.ExportImageCompleted(_logger, layerId, result.Data.Length);
                scope.SetSuccess(1);
                return Results.File(result.Data, result.ContentType);
            }

            var imageUrl = await _temporaryFileService.StoreTemporaryFileAsync(
                result.Data,
                result.ContentType,
                TimeSpan.FromHours(1),
                principal: context.User,
                cancellationToken: cancellationToken);

            var extent = result.Extent ?? aggregateExtent;
            if (extent == null && selectedRasters.Length == 1)
            {
                extent = await _rasterStore.GetExtentAsync(layerId, selectedRasters[0].Id, cancellationToken);
            }

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
                        LatestWkid = extent?.Srid ?? result.Srid ?? 4326,
                    },
                },
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

    private static bool TryParseExportParameters(
        ExportImageRequest request,
        out RasterQuery query,
        out ExportParameterParseError error)
    {
        query = default;
        error = default;

        try
        {
            if (!string.IsNullOrWhiteSpace(request.RenderingRule))
            {
                error = new ExportParameterParseError(
                    "renderingRule is not implemented on this service.",
                    IsNotImplemented: true);
                return false;
            }

            if (!TryParseRequestedSize(request.Size, out var requestedWidth, out var requestedHeight, out var sizeError))
            {
                error = new ExportParameterParseError(sizeError ?? "Invalid size parameter.");
                return false;
            }

            if (request.CompressionQuality.HasValue &&
                (request.CompressionQuality.Value < MinCompressionQuality || request.CompressionQuality.Value > MaxCompressionQuality))
            {
                error = new ExportParameterParseError("compressionQuality must be between 0 and 100.");
                return false;
            }

            var outputSrid = SpatialReferenceHelpers.TryParseSrid(request.ImageSr);
            if (!string.IsNullOrWhiteSpace(request.ImageSr) && !outputSrid.HasValue)
            {
                error = new ExportParameterParseError("imageSr must be a valid spatial reference.");
                return false;
            }

            var bboxSrid = SpatialReferenceHelpers.TryParseSrid(request.BboxSr);
            if (!string.IsNullOrWhiteSpace(request.BboxSr) && !bboxSrid.HasValue)
            {
                error = new ExportParameterParseError("bboxSr must be a valid spatial reference.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.PixelType) &&
                !string.Equals(request.PixelType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                error = new ExportParameterParseError(
                    "pixelType conversion is not implemented on this service. Omit pixelType or use UNKNOWN.",
                    IsNotImplemented: true);
                return false;
            }

            if (!RasterParsingHelpers.TryParseRasterFormat(request.Format ?? "png", out var outputFormat) ||
                outputFormat is RasterFormat.COG or RasterFormat.Raw)
            {
                error = new ExportParameterParseError(
                    "format must be one of the supported export formats: png, jpg, jpeg, tiff, tif.");
                return false;
            }

            if (!TryResolveTiffCompression(request.Compression, outputFormat, out var tiffCompression, out var compressionError))
            {
                error = new ExportParameterParseError(compressionError ?? "compression is invalid.");
                return false;
            }

            query = new RasterQuery
            {
                OutputFormat = outputFormat,
                Quality = request.CompressionQuality,
                OutputSrid = outputSrid,
                OutputWidth = requestedWidth,
                OutputHeight = requestedHeight,
                TiffCompression = tiffCompression,
            };

            if (!string.IsNullOrEmpty(request.Bbox))
            {
                if (!RasterParsingHelpers.TryParseBoundingBox(request.Bbox, out var minX, out var minY, out var maxX, out var maxY))
                {
                    error = new ExportParameterParseError("bbox must be in format xmin,ymin,xmax,ymax.");
                    return false;
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
                        Srid = bboxSrid,
                    },
                };
            }

            if (!string.IsNullOrEmpty(request.Interpolation))
            {
                query = query with { ResamplingAlgorithm = ParseInterpolation(request.Interpolation) };
            }

            return true;
        }
        catch (FormatException)
        {
            error = new ExportParameterParseError("Invalid numeric export parameter.");
            return false;
        }
        catch (OverflowException)
        {
            error = new ExportParameterParseError("Export parameter is outside the supported range.");
            return false;
        }
        catch (ArgumentException)
        {
            error = new ExportParameterParseError("Invalid export parameter.");
            return false;
        }
    }

    private static bool TryResolveTiffCompression(
        string? compression,
        RasterFormat outputFormat,
        out TiffCompression? tiffCompression,
        out string? errorMessage)
    {
        tiffCompression = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(compression))
        {
            return true;
        }

        if (outputFormat != RasterFormat.TIFF)
        {
            return true;
        }

        tiffCompression = compression.Trim().ToUpperInvariant() switch
        {
            "NONE" => TiffCompression.None,
            "JPEG" => TiffCompression.JPEG,
            "LZ77" => TiffCompression.Deflate,
            _ => null
        };

        if (tiffCompression.HasValue)
        {
            return true;
        }

        errorMessage = "compression must be one of: None, JPEG, or LZ77.";
        return false;
    }

    // Public /exportImage follows the ArcGIS contract: size is "width,height" and
    // defaults to 400,400 when omitted.
    private static bool TryParseRequestedSize(string? size, out int width, out int height, out string? error)
    {
        width = DefaultOutputDimension;
        height = DefaultOutputDimension;
        error = null;

        if (string.IsNullOrWhiteSpace(size))
        {
            return true;
        }

        var parts = size.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            error = "size must be a comma-separated width,height pair.";
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
            w < MinOutputDimension ||
            w > MaxAllowedOutputDimension)
        {
            error = $"size width must be between {MinOutputDimension} and {MaxAllowedOutputDimension}.";
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            h < MinOutputDimension ||
            h > MaxAllowedOutputDimension)
        {
            error = $"size height must be between {MinOutputDimension} and {MaxAllowedOutputDimension}.";
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    private static ResamplingAlgorithm ParseInterpolation(string interpolation)
    {
        return interpolation switch
        {
            "RSP_NearestNeighbor" => ResamplingAlgorithm.NearestNeighbor,
            "RSP_BilinearInterpolation" => ResamplingAlgorithm.Bilinear,
            "RSP_CubicConvolution" => ResamplingAlgorithm.Bicubic,
            _ => ResamplingAlgorithm.Bilinear,
        };
    }

    private static bool WantsInlineImageResponse(string? format)
        => string.Equals(format, InlineImageFormat, StringComparison.OrdinalIgnoreCase);

    private readonly record struct ExportParameterParseError(string Detail, bool IsNotImplemented = false);
}
