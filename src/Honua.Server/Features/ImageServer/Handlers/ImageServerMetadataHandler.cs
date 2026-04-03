// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server metadata operations.
/// Provides service information and capabilities for raster layers.
/// </summary>
internal sealed class ImageServerMetadataHandler
{
    /// <summary>ArcGIS REST API version for compatibility.</summary>
    private const double ArcGisVersion = 10.81;

    /// <summary>Minimum pixel size advertised in service metadata (finest resolution).</summary>
    private const double MinPixelSize = 0.1;

    /// <summary>Maximum pixel size advertised in service metadata (coarsest resolution).</summary>
    private const double MaxPixelSize = 1000.0;

    /// <summary>Maximum image height in pixels for export requests.</summary>
    private const int MaxImageHeight = 4100;

    /// <summary>Maximum image width in pixels for export requests.</summary>
    private const int MaxImageWidth = 15000;
    private const int MaxTileZoom = 23;
    private const int TileSize = 256;
    private const int TileDpi = 96;

    /// <summary>Maximum number of records returned in catalog queries.</summary>
    private const int MaxRecordCount = 1000;

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerMetadataHandler> _logger;

    public ImageServerMetadataHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<ImageServerMetadataHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets comprehensive service metadata for an Image Server.
    /// </summary>
    public async Task<IResult> GetServiceInfoAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

        try
        {
            // Get layer definition
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // Start telemetry activity
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "metadata",
                HonuaTelemetry.Protocols.ImageServer,
                layerId.ToString(CultureInfo.InvariantCulture));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "get-service-info");

            // Get raster information
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            // Use the first raster for service metadata (could be enhanced for multi-raster scenarios)
            var primaryRaster = rasters[0];

            // Get raster statistics for min/max values
            var statistics = await _rasterStore.GetStatisticsAsync(layerId, primaryRaster.Id, cancellationToken: cancellationToken);

            // Get raster extent
            var extent = await _rasterStore.GetExtentAsync(layerId, primaryRaster.Id, cancellationToken);
            if (extent == null)
            {
                ImageServerLog.ExtentNotAvailable(_logger, layerId);
                return StandardErrorHelpers.CreateInternalServerError(context, "Unable to determine raster extent.");
            }

            // Build service info response
            var serviceInfo = new ImageServerServiceInfo
            {
                CurrentVersion = ArcGisVersion,
                ServiceDescription = layer.Description ?? $"Image service for {layer.Name}",
                Name = layer.Name,
                Description = layer.Description,
                Extent = new ImageServerExtent
                {
                    XMin = extent.Value.XMin,
                    YMin = extent.Value.YMin,
                    XMax = extent.Value.XMax,
                    YMax = extent.Value.YMax,
                    SpatialReference = CreateSpatialReference(extent.Value.Srid)
                },
                SpatialReference = CreateSpatialReference(primaryRaster.Srid),
                PixelSizeX = CalculatePixelSize(extent.Value, primaryRaster.Width),
                PixelSizeY = CalculatePixelSize(extent.Value, primaryRaster.Height, isHeight: true),
                BandCount = primaryRaster.BandCount,
                PixelType = MapPixelType(primaryRaster.PixelType),
                MinPixelSize = MinPixelSize,
                MaxPixelSize = MaxPixelSize,
                CopyrightText = layer.Description ?? "",
                ServiceDataType = "esriImageServiceDataTypeGeneric",
                MinValues = statistics.Select(s => s.MinValue ?? 0).ToArray(),
                MaxValues = statistics.Select(s => s.MaxValue ?? 0).ToArray(),
                MeanValues = statistics.Select(s => s.MeanValue ?? 0).ToArray(),
                StdvValues = statistics.Select(s => s.StandardDeviation ?? 0).ToArray(),
                Capabilities = "Catalog,Image,Metadata,Pixels,Tilemap",
                MaxImageHeight = MaxImageHeight,
                MaxImageWidth = MaxImageWidth,
                MaxRecordCount = MaxRecordCount,
                SingleFusedMapCache = true,
                CacheType = "Map",
                TileInfo = BuildTileInfo()
            };

            ImageServerLog.ServiceInfoGenerated(_logger, layerId, primaryRaster.BandCount, statistics.Length);

            // Record telemetry success
            HonuaTelemetry.SetSuccess(featureActivity, 1);

            return Results.Json(serviceInfo, ImageServerJsonContext.Default.ImageServerServiceInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.ServiceInfoFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving service information.");
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    private static SpatialReference CreateSpatialReference(int? srid)
    {
        return new SpatialReference
        {
            Wkid = srid ?? 4326, // Default to WGS84
            LatestWkid = srid ?? 4326
        };
    }

    private static double CalculatePixelSize(Core.Features.Raster.Domain.RasterExtent extent, int pixelCount, bool isHeight = false)
    {
        if (pixelCount <= 0)
            return 0;

        if (isHeight)
        {
            return (extent.YMax - extent.YMin) / pixelCount;
        }
        else
        {
            return (extent.XMax - extent.XMin) / pixelCount;
        }
    }

    private static string MapPixelType(string postgisPixelType)
    {
        // Map PostGIS pixel types to Esri pixel types
        return postgisPixelType.ToUpperInvariant() switch
        {
            "8BUI" => "U8",
            "8BSI" => "S8",
            "16BUI" => "U16",
            "16BSI" => "S16",
            "32BUI" => "U32",
            "32BSI" => "S32",
            "32BF" => "F32",
            "64BF" => "F64",
            _ => "U8" // Default fallback
        };
    }

    private static TileInfo BuildTileInfo()
    {
        const double webMercatorOrigin = global::Honua.Core.Features.Shared.Models.SpatialConstants.WebMercatorExtent;
        const double pixelSize = 0.00028;

        var lods = new LevelOfDetail[MaxTileZoom + 1];
        for (var z = 0; z <= MaxTileZoom; z++)
        {
            var matrixSize = 1L << z;
            var resolution = 2.0 * webMercatorOrigin / (TileSize * (double)matrixSize);
            var scale = resolution / pixelSize;
            lods[z] = new LevelOfDetail
            {
                Level = z,
                Resolution = resolution,
                Scale = scale
            };
        }

        return new TileInfo
        {
            Rows = TileSize,
            Cols = TileSize,
            Dpi = TileDpi,
            Format = "PNG",
            Origin = new Point
            {
                X = -webMercatorOrigin,
                Y = webMercatorOrigin
            },
            SpatialReference = new SpatialReference
            {
                Wkid = 3857,
                LatestWkid = 3857
            },
            Lods = lods
        };
    }
}
