// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server export image operations.
/// Provides raster image export with clipping, reprojection, and format conversion.
/// </summary>
internal sealed class ImageServerExportHandler
{
    private const int DefaultMaxOutputDimension = 1024;

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
                return Results.NotFound();
            }

            // Get raster data
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return Results.NotFound();
            }

            // Use the first raster (could be enhanced for multi-raster scenarios)
            var primaryRaster = rasters[0];

            // Parse export parameters
            var query = ParseExportParameters(request);
            if (query == null)
            {
                ImageServerLog.InvalidExportParameters(_logger, layerId, "Unable to parse export parameters");
                return Results.BadRequest("Invalid export parameters");
            }

            // Determine output dimensions and propagate to query
            var (width, height) = CalculateOutputDimensions(request, primaryRaster);
            var exportQuery = query.Value with { OutputWidth = width, OutputHeight = height };
            var formatName = exportQuery.OutputFormat.ToString();
            ImageServerLog.ExportImageStarted(_logger, layerId, width, height, formatName);

            // Export the image
            var result = await _rasterStore.ExportImageAsync(layerId, primaryRaster.Id, exportQuery, cancellationToken);

            // Store the image temporarily and get the public URL
            var imageUrl = await _temporaryFileService.StoreTemporaryFileAsync(
                result.Data,
                result.ContentType,
                TimeSpan.FromHours(1),
                cancellationToken);

            var extent = result.Extent;
            extent ??= await _rasterStore.GetExtentAsync(layerId, primaryRaster.Id, cancellationToken);

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

            return Results.Ok(exportResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.ExportImageFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return Results.Problem("An error occurred while exporting the image.", statusCode: 500);
        }
    }

    private static RasterQuery? ParseExportParameters(ExportImageRequest request)
    {
        try
        {
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

            var query = new RasterQuery
            {
                OutputFormat = RasterParsingHelpers.ParseRasterFormat(request.Format ?? "png"),
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

    private static (int width, int height) CalculateOutputDimensions(ExportImageRequest request, Core.Features.Raster.Domain.RasterInfo raster)
    {
        // Guard against invalid raster metadata
        if (raster.Width <= 0 || raster.Height <= 0)
        {
            return (DefaultMaxOutputDimension, DefaultMaxOutputDimension);
        }

        // If size is specified, use it as width and calculate proportional height
        if (request.Size.HasValue)
        {
            var width = request.Size.Value;
            var aspectRatio = (double)raster.Height / raster.Width;
            var height = (int)(width * aspectRatio);
            return (width, height);
        }

        // Default to original raster dimensions capped at max output size
        return (Math.Min(raster.Width, DefaultMaxOutputDimension), Math.Min(raster.Height, DefaultMaxOutputDimension));
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

}
