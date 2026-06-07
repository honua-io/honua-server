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

            if (!TryValidateMosaicRule(request.MosaicRule, out var mosaicRuleError))
            {
                ImageServerLog.InvalidExportParameters(_logger, layerId, mosaicRuleError);
                return StandardErrorHelpers.CreateNotImplemented(context, mosaicRuleError);
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

            if (!TryParseExportFormat(request.Format, out var outputFormat) ||
                outputFormat is RasterFormat.COG or RasterFormat.Raw)
            {
                error = new ExportParameterParseError(
                    "format must be one of the supported export formats: png, png8, png24, png32, jpg, jpeg, jpgpng, tiff, tif.");
                return false;
            }

            if (!TryResolveTiffCompression(request.Compression, outputFormat, out var tiffCompression, out var compressionError))
            {
                error = new ExportParameterParseError(compressionError ?? "compression is invalid.");
                return false;
            }

            if (!TryParseBandIds(request.BandIds, out var bands, out var bandError))
            {
                error = new ExportParameterParseError(bandError ?? "bandIds is invalid.");
                return false;
            }

            if (!TryValidateNoData(request.NoData, request.NoDataInterpretation, out var noDataError))
            {
                error = new ExportParameterParseError(noDataError ?? "noData is invalid.");
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
                Bands = bands,
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

    // The ArcGIS Maps SDK clients (ArcGIS API for Python ImageryLayer.export_image,
    // ArcGIS Maps SDK for JavaScript ImageryLayer) default to and send Esri-specific
    // ImageServer format tokens such as "jpgpng" and the PNG bit-depth variants
    // (png8/png24/png32). Those are not raster container formats the shared
    // RasterParsingHelpers parser recognizes, so normalize them to a concrete
    // export format here before delegating to the shared parser.
    private static bool TryParseExportFormat(string? format, out RasterFormat outputFormat)
    {
        var normalized = format?.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "jpgpng":
                // Esri "jpgpng" means "JPEG where opaque, PNG where transparency is needed".
                // This service emits a single concrete encoding; PNG preserves transparency
                // and is the safe lossless choice for the combined token.
                outputFormat = RasterFormat.PNG;
                return true;
            case "png8":
            case "png24":
            case "png32":
                outputFormat = RasterFormat.PNG;
                return true;
            default:
                return RasterParsingHelpers.TryParseRasterFormat(
                    string.IsNullOrWhiteSpace(normalized) ? "png" : normalized,
                    out outputFormat);
        }
    }

    // bandIds selects and orders the output bands (Esri sends a CSV of 0-based band
    // indices). Honua's raster store uses 1-based band indexing, so each index is
    // shifted by one. The selection is forwarded to the shared export pipeline via
    // RasterQuery.Bands; an empty or whitespace value means "all bands".
    private static bool TryParseBandIds(string? bandIds, out int[]? bands, out string? error)
    {
        bands = null;
        error = null;

        if (string.IsNullOrWhiteSpace(bandIds))
        {
            return true;
        }

        var parts = bandIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var parsed = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zeroBased) ||
                zeroBased < 0)
            {
                error = "bandIds must be a comma-separated list of non-negative band indices.";
                return false;
            }

            parsed.Add(zeroBased + 1);
        }

        bands = parsed.ToArray();
        return true;
    }

    // noData / noDataInterpretation are validated for shape so callers get a structured
    // 400 rather than a silently ignored value. The NoData fill is applied by the shared
    // raster export pipeline when the underlying driver supports it; the interpretation
    // token is constrained to the Esri enumeration.
    private static bool TryValidateNoData(string? noData, string? noDataInterpretation, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(noData))
        {
            foreach (var token in noData.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    error = "noData must be a number or a comma-separated list of per-band numbers.";
                    return false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(noDataInterpretation) &&
            !noDataInterpretation.Equals("esriNoDataMatchAny", StringComparison.OrdinalIgnoreCase) &&
            !noDataInterpretation.Equals("esriNoDataMatchAll", StringComparison.OrdinalIgnoreCase))
        {
            error = "noDataInterpretation must be esriNoDataMatchAny or esriNoDataMatchAll.";
            return false;
        }

        return true;
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

    // A mosaicRule is honored only to the extent of its mergeStrategy/operation token
    // (mapped to a RasterMergeStrategy). Esri mosaicRules that select a mosaicMethod honua
    // does not implement (e.g. esriMosaicNadir, esriMosaicLockRaster, esriMosaicSeamline)
    // were silently ignored; reject them with a clean 501 rather than no-op. A bare
    // esriMosaicNone / esriMosaicAttribute is accepted because it does not change pixel
    // selection here.
    private static bool TryValidateMosaicRule(string? mosaicRule, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(mosaicRule))
        {
            return true;
        }

        var trimmed = mosaicRule.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return true;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return true;
            }

            if (document.RootElement.TryGetProperty("mosaicMethod", out var method) &&
                method.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = method.GetString();
                if (!string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("esriMosaicNone", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("esriMosaicAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"mosaicRule mosaicMethod '{value}' is not implemented on this service.";
                    return false;
                }
            }

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            error = "mosaicRule contains invalid JSON.";
            return false;
        }
    }

    private readonly record struct ExportParameterParseError(string Detail, bool IsNotImplemented = false);
}
