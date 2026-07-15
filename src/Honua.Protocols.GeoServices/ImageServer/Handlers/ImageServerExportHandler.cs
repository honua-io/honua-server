// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ImageServerExportBackend _exportBackend;
    private readonly ITemporaryFileService _temporaryFileService;
    private readonly ILogger<ImageServerExportHandler> _logger;

    public ImageServerExportHandler(
        IMetadataV2GraphProvider graphProvider,
        ImageServerExportBackend exportBackend,
        ITemporaryFileService temporaryFileService,
        ILogger<ImageServerExportHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _exportBackend = exportBackend ?? throw new ArgumentNullException(nameof(exportBackend));
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

            if (!ImageServerMultidimensionalDefinition.TryParse(
                    request.MultidimensionalDefinition,
                    out var dimensionConstraints,
                    out var multidimensionalError))
            {
                ImageServerLog.InvalidExportParameters(
                    _logger,
                    layerId,
                    multidimensionalError ?? "Invalid multidimensionalDefinition");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    multidimensionalError ?? "Invalid multidimensionalDefinition.");
            }

            if (dimensionConstraints.Count > 0)
            {
                return await ExportMultidimensionalSliceAsync(
                        context,
                        layerId,
                        request,
                        exportQuery,
                        dimensionConstraints,
                        scope,
                        cancellationToken)
                    .ConfigureAwait(false);
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

            if (!ImageServerMosaicRule.TryParse(request.MosaicRule, out var mosaicRule, out var mosaicRuleError, out var mosaicRuleNotImplemented))
            {
                ImageServerLog.InvalidExportParameters(_logger, layerId, mosaicRuleError);
                return mosaicRuleNotImplemented
                    ? StandardErrorHelpers.CreateNotImplemented(context, mosaicRuleError)
                    : StandardErrorHelpers.CreateBadRequest(context, mosaicRuleError);
            }

            // The request override (mosaicRule mergeStrategy/operation token) wins; otherwise
            // fall back to the resource-default merge strategy.
            var mergeStrategy = mosaicRule.Operation
                ?? ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);
            var ordering = mosaicRule.ToOrdering();

            // esriMosaicLockRaster composites a caller-pinned set of rasters and is independent
            // of the temporal newest-batch snapshot, so the selection query drops the timestamp
            // filter and the locked ids are intersected with the spatial selection below.
            var selectionQuery = new RasterSelectionQuery
            {
                Geometry = exportQuery.ClipRegion?.Geometry,
                GeometrySrid = exportQuery.ClipRegion?.Srid,
                Timestamp = mosaicRule.Method == MosaicMethod.LockRaster ? null : timestamp,
            };

            var selectedRasters = await _exportBackend.QueryRastersAsync(layerId, selectionQuery, cancellationToken);

            if (mosaicRule.Method == MosaicMethod.LockRaster)
            {
                var lockedIds = mosaicRule.LockRasterIds ?? Array.Empty<long>();
                var lockedSet = new HashSet<long>(lockedIds);
                selectedRasters = selectedRasters.Where(r => lockedSet.Contains(r.Id)).ToArray();
            }

            if (selectedRasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            // A genuinely-unsupported mosaic method (nadir, center, arbitrary non-date
            // attribute sort) can only affect output when more than one raster is composited;
            // with a single raster the method cannot change pixel selection, so it is accepted.
            if (mosaicRule.Method == MosaicMethod.Unsupported && selectedRasters.Length > 1)
            {
                var unsupportedMessage = "mosaicRule mosaicMethod is not implemented on this service.";
                ImageServerLog.InvalidExportParameters(_logger, layerId, unsupportedMessage);
                return StandardErrorHelpers.CreateNotImplemented(context, unsupportedMessage);
            }

            var aggregateExtent = ImageServerMosaicHelpers.ComputeAggregateExtent(selectedRasters);

            var outputFormat = exportQuery.OutputFormat.ToString();
            ImageServerLog.ExportImageStarted(
                _logger,
                layerId,
                exportQuery.OutputWidth ?? DefaultOutputDimension,
                exportQuery.OutputHeight ?? DefaultOutputDimension,
                outputFormat);

            var result = await _exportBackend.ExportAsync(
                layerId,
                selectedRasters,
                mergeStrategy,
                exportQuery,
                ordering,
                mosaicRule.ToAttributeSort(),
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
                extent = await _exportBackend.GetExtentAsync(layerId, selectedRasters[0].Id, cancellationToken);
            }

            // Esri exportImage is expected to echo the export extent. When the raster store
            // returns no real extent, fall back to the requested bbox (what callers asked to
            // render) rather than emitting a fabricated [0,0,1,1] box. If neither is available,
            // omit the extent entirely instead of inventing a 1×1 envelope.
            var exportResponse = new ExportImageResponse
            {
                Href = imageUrl,
                Width = result.Width,
                Height = result.Height,
                Extent = BuildExtent(extent, request.Bbox, bboxSrid: SpatialReferenceHelpers.TryParseSrid(request.BboxSr), result.Srid),
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
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.ExportImageFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while exporting the image.");
        }
    }

    private async Task<IResult> ExportMultidimensionalSliceAsync(
        HttpContext context,
        int layerId,
        ExportImageRequest request,
        RasterQuery exportQuery,
        IReadOnlyList<ImageServerDimensionConstraint> constraints,
        HonuaTelemetryScope scope,
        CancellationToken cancellationToken)
    {
        if (constraints.Any(static constraint => constraint.Values.Length != 1))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "exportImage slice reads require exactly one coordinate value per multidimensionalDefinition entry.");
        }

        if (!string.IsNullOrWhiteSpace(request.Time) || !string.IsNullOrWhiteSpace(request.MosaicRule))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "time and mosaicRule cannot be combined with multidimensionalDefinition; select dimensions in multidimensionalDefinition.");
        }

        // A coordinate-selected Zarr slice renders a single variable, so multi-band raster
        // functions and per-band NoData/band selection do not apply. Reprojection, JPEG/TIFF
        // output, interpolation, and the single-band Stretch/Colormap functions ARE supported
        // (threaded onto the canonical slice request below); only genuinely-inapplicable
        // transforms are rejected.
        if (exportQuery.Bands is { Length: > 0 })
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "band selection (bandIds / ExtractBand) is not supported for single-variable multidimensional Zarr exports.");
        }

        if (exportQuery.BandArithmetic is not null || exportQuery.Terrain is not null)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "BandArithmetic and terrain raster functions are not supported for multidimensional Zarr exports.");
        }

        if (exportQuery.RenderingClip is not null)
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "renderingRule Clip is not supported for multidimensional Zarr exports; trim with bbox instead.");
        }

        if (!string.IsNullOrWhiteSpace(request.NoData))
        {
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "noData transforms are not supported for multidimensional Zarr exports; NoData is rendered transparent from the coverage fill value.");
        }

        var imageSrid = exportQuery.OutputSrid;
        var bboxSrid = SpatialReferenceHelpers.TryParseSrid(request.BboxSr);

        RasterExtent? bounds = null;
        if (!string.IsNullOrWhiteSpace(request.Bbox) &&
            RasterParsingHelpers.TryParseBoundingBox(
                request.Bbox,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            bounds = new RasterExtent
            {
                XMin = minX,
                YMin = minY,
                XMax = maxX,
                YMax = maxY,
                Srid = bboxSrid ?? imageSrid,
            };
        }

        var selections = constraints.Select(static constraint => new ZarrPointSliceSelection(
            string.IsNullOrWhiteSpace(constraint.VariableName) ? null : constraint.VariableName,
            constraint.DimensionName,
            constraint.Values[0])).ToArray();
        scope.WithTag("honua.slice.selection_count", selections.Length);

        // Reprojection between bboxSR/imageSR uses the shared coordinate-transform service.
        // It is registered by the PostGIS provider; read-only providers that lack it simply
        // cannot reproject, and the reader rejects a non-native output CRS with a clean 400.
        var coordinateTransform = context.RequestServices.GetService<ICoordinateTransformService>();
        var read = await _exportBackend.ReadZarrSliceAsync(
                layerId,
                new ZarrRasterSliceReadRequest(
                    bounds,
                    bboxSrid,
                    exportQuery.OutputWidth ?? DefaultOutputDimension,
                    exportQuery.OutputHeight ?? DefaultOutputDimension,
                    selections,
                    OutputSrid: imageSrid ?? bboxSrid,
                    Resampling: exportQuery.ResamplingAlgorithm,
                    Stretch: exportQuery.Stretch,
                    Colormap: exportQuery.Colormap),
                coordinateTransform,
                cancellationToken)
            .ConfigureAwait(false);
        if (read.Status != ZarrRasterSliceReadStatus.Success)
        {
            var message = read.Error ?? "The multidimensional Zarr slice could not be exported.";
            ImageServerLog.InvalidExportParameters(_logger, layerId, message);
            return read.Status switch
            {
                ZarrRasterSliceReadStatus.RegistrationNotFound or ZarrRasterSliceReadStatus.ReaderUnavailable
                    => StandardErrorHelpers.CreateNotImplemented(context, message),
                ZarrRasterSliceReadStatus.ReadFailed
                    => StandardErrorHelpers.CreateInternalServerError(context, message),
                _ => StandardErrorHelpers.CreateBadRequest(context, message),
            };
        }

        var raster = read.Raster!.Value;
        if (!TryEncodeSliceOutput(
                exportQuery,
                raster,
                read.Rgba,
                out var outputData,
                out var outputContentType,
                out var encodeError))
        {
            ImageServerLog.InvalidExportParameters(_logger, layerId, encodeError);
            return StandardErrorHelpers.CreateInternalServerError(context, encodeError);
        }

        var outputFormatName = exportQuery.OutputFormat switch
        {
            RasterFormat.JPEG => "JPEG",
            RasterFormat.TIFF => "TIFF",
            _ => "PNG",
        };
        ImageServerLog.ExportImageStarted(_logger, layerId, raster.Width, raster.Height, outputFormatName);
        scope.WithTag("honua.slice.variable", read.Variable);
        scope.WithTag("honua.output.bytes", outputData.Length);

        if (WantsInlineImageResponse(request.F))
        {
            ImageServerLog.ExportImageCompleted(_logger, layerId, outputData.Length);
            scope.SetSuccess(1);
            return Results.File(outputData, outputContentType);
        }

        var imageUrl = await _temporaryFileService.StoreTemporaryFileAsync(
            outputData,
            outputContentType,
            TimeSpan.FromHours(1),
            principal: context.User,
            cancellationToken: cancellationToken);
        var response = new ExportImageResponse
        {
            Href = imageUrl,
            Width = raster.Width,
            Height = raster.Height,
            Extent = BuildExtent(raster.Extent, request.Bbox, bboxSrid, raster.Srid),
        };

        ImageServerLog.ExportImageCompleted(_logger, layerId, outputData.Length);
        scope.SetSuccess(1);
        return Results.Json(response, ImageServerJsonContext.Default.ExportImageResponse);
    }

    // Encodes the rendered slice into the requested container. PNG is emitted directly by the
    // canonical slice reader; JPEG and TIFF are re-encoded from the reader's RGBA buffer. Only
    // the three exportImage raster formats reach here (validated during parameter parsing).
    private static bool TryEncodeSliceOutput(
        RasterQuery exportQuery,
        RasterResult raster,
        byte[]? rgba,
        out byte[] data,
        out string contentType,
        out string error)
    {
        error = string.Empty;

        switch (exportQuery.OutputFormat)
        {
            case RasterFormat.PNG:
                data = raster.Data;
                contentType = raster.ContentType;
                return true;
            case RasterFormat.JPEG when rgba is not null:
                (data, contentType) = ImageServerRasterSliceEncoder.EncodeJpeg(
                    rgba, raster.Width, raster.Height, exportQuery.Quality);
                return true;
            case RasterFormat.TIFF when rgba is not null:
                (data, contentType) = ImageServerRasterSliceEncoder.EncodeTiff(
                    rgba, raster.Width, raster.Height, exportQuery.TiffCompression);
                return true;
            default:
                data = Array.Empty<byte>();
                contentType = string.Empty;
                error = "The multidimensional Zarr slice could not be encoded in the requested output format.";
                return false;
        }
    }

    // Builds the export response extent. Prefers a real extent from the raster store; when
    // none is available falls back to the requested bbox (which Esri exportImage is expected
    // to echo). Returns null when neither is available so callers can omit the property rather
    // than emit a fabricated [0,0,1,1] envelope.
    private static ImageServerExtent? BuildExtent(
        RasterExtent? extent,
        string? requestedBbox,
        int? bboxSrid,
        int? resultSrid)
    {
        if (extent is { } realExtent)
        {
            return new ImageServerExtent
            {
                XMin = realExtent.XMin,
                YMin = realExtent.YMin,
                XMax = realExtent.XMax,
                YMax = realExtent.YMax,
                SpatialReference = new SpatialReference
                {
                    Wkid = realExtent.Srid ?? resultSrid ?? 4326,
                    LatestWkid = realExtent.Srid ?? resultSrid ?? 4326,
                },
            };
        }

        if (!string.IsNullOrEmpty(requestedBbox) &&
            RasterParsingHelpers.TryParseBoundingBox(requestedBbox, out var minX, out var minY, out var maxX, out var maxY))
        {
            return new ImageServerExtent
            {
                XMin = minX,
                YMin = minY,
                XMax = maxX,
                YMax = maxY,
                SpatialReference = new SpatialReference
                {
                    Wkid = bboxSrid ?? resultSrid ?? 4326,
                    LatestWkid = bboxSrid ?? resultSrid ?? 4326,
                },
            };
        }

        return null;
    }

    // Translates an Esri renderingRule (Identity/Stretch chain) into the canonical
    // RasterStretch threaded onto the query. Unsupported chains surface a 400 (unknown
    // function) or 501 (recognized but unimplemented function/option).
    private static bool TryMapRenderingRule(
        string renderingRuleJson,
        out RasterStretch? stretch,
        out RasterColormap? colormap,
        out RasterClipRegion? clipRegion,
        out int[]? bands,
        out RasterBandArithmetic? bandArithmetic,
        out RasterTerrainFunction? terrain,
        out ExportParameterParseError error)
    {
        stretch = null;
        colormap = null;
        clipRegion = null;
        bands = null;
        bandArithmetic = null;
        terrain = null;
        error = default;

        RasterFunctionDocument document;
        try
        {
            document = JsonSerializer.Deserialize(renderingRuleJson, ImageServerJsonContext.Default.RasterFunctionDocument)
                ?? throw new JsonException("Empty renderingRule document.");
        }
        catch (JsonException)
        {
            error = new ExportParameterParseError("renderingRule must be a valid raster function document.");
            return false;
        }

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);
        if (!mapping.Supported)
        {
            error = new ExportParameterParseError(
                mapping.Reason ?? "renderingRule is not supported on this service.",
                IsNotImplemented: mapping.IsNotImplemented);
            return false;
        }

        stretch = mapping.Stretch;
        colormap = mapping.Colormap;
        clipRegion = mapping.ClipRegion;
        bands = mapping.Bands;
        bandArithmetic = mapping.BandArithmetic;
        terrain = mapping.Terrain;
        return true;
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
            RasterStretch? renderingStretch = null;
            RasterColormap? renderingColormap = null;
            RasterClipRegion? renderingClip = null;
            int[]? renderingBands = null;
            RasterBandArithmetic? renderingBandArithmetic = null;
            RasterTerrainFunction? renderingTerrain = null;
            if (!string.IsNullOrWhiteSpace(request.RenderingRule) &&
                !TryMapRenderingRule(request.RenderingRule, out renderingStretch, out renderingColormap, out renderingClip, out renderingBands, out renderingBandArithmetic, out renderingTerrain, out error))
            {
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

            // An ExtractBand renderingRule and the bandIds parameter express the same
            // band-selection intent. When both are present the renderingRule chain wins,
            // matching ArcGIS clients that author band selection through the function chain.
            var effectiveBands = renderingBands ?? bands;

            // BandArithmetic selects its own two source bands by index, so it cannot be
            // combined with an explicit band selection/order (ExtractBand or bandIds) without
            // an ambiguous band layout. Reject the conflict with a clean 400.
            if (renderingBandArithmetic is not null && effectiveBands is { Length: > 0 })
            {
                error = new ExportParameterParseError(
                    "BandArithmetic cannot be combined with an explicit band selection (ExtractBand or bandIds).");
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
                Bands = effectiveBands,
                // ArcGIS exportImage defaults to bilinear interpolation. Keep that
                // default in the canonical query while preserving whether the client
                // explicitly supplied interpolation for multidimensional slice validation.
                ResamplingAlgorithm = ResamplingAlgorithm.Bilinear,
                BandArithmetic = renderingBandArithmetic,
                Terrain = renderingTerrain,
                Stretch = renderingStretch,
                Colormap = renderingColormap,
                RenderingClip = renderingClip,
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

        if (!string.IsNullOrWhiteSpace(noData) &&
            noData.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(token => !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            error = "noData must be a number or a comma-separated list of per-band numbers.";
            return false;
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

    private readonly record struct ExportParameterParseError(string Detail, bool IsNotImplemented = false);
}
