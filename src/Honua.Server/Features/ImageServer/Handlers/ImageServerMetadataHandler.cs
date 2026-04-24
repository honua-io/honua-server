// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
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
    private const int MaxImageHeight = 4096;

    /// <summary>Maximum image width in pixels for export requests.</summary>
    private const int MaxImageWidth = 4096;

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
                // NOTE: Mensuration is intentionally omitted until the /measure endpoint
                // is implemented. Re-add it alongside the handler so capability advertising
                // stays in lockstep with routed operations.
                Capabilities = "Catalog,Image,Metadata,Pixels,Statistics",
                MaxImageHeight = MaxImageHeight,
                MaxImageWidth = MaxImageWidth,
                MaxRecordCount = MaxRecordCount,
                SingleFusedMapCache = false,
                CacheType = null,
                TileInfo = null,
                HasHistograms = true,
                TimeInfo = BuildTimeInfo(layer.Metadata?.TimeInfo)
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

    /// <summary>
    /// Builds the Esri-conformant <c>timeInfo</c> block when the layer declares
    /// temporal fields. The temporal extent is intentionally left null because
    /// raster catalog metadata does not yet carry per-item timestamps; clients
    /// can still probe the field names without breakage.
    /// </summary>
    private static ImageServerTimeInfo? BuildTimeInfo(LayerTimeInfo? layerTimeInfo)
    {
        if (layerTimeInfo is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(layerTimeInfo.StartTimeField) &&
            string.IsNullOrWhiteSpace(layerTimeInfo.EndTimeField) &&
            string.IsNullOrWhiteSpace(layerTimeInfo.TrackIdField))
        {
            return null;
        }

        return new ImageServerTimeInfo
        {
            StartTimeField = layerTimeInfo.StartTimeField,
            EndTimeField = layerTimeInfo.EndTimeField,
            TrackIdField = layerTimeInfo.TrackIdField,
            TimeReference = new ImageServerTimeReference()
        };
    }

}
