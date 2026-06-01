// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

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

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly IImageServerMultidimensionalInfoBuilder _multidimensionalInfoBuilder;
    private readonly ILogger<ImageServerMetadataHandler> _logger;

    public ImageServerMetadataHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        IImageServerMultidimensionalInfoBuilder multidimensionalInfoBuilder,
        ILogger<ImageServerMetadataHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _multidimensionalInfoBuilder = multidimensionalInfoBuilder ?? throw new ArgumentNullException(nameof(multidimensionalInfoBuilder));
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
            // Get layer definition from the Metadata v2 graph
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
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

            var aggregateExtent = ImageServerMosaicHelpers.ComputeAggregateExtent(rasters);
            var referenceRaster = CreateMosaicReferenceRaster(rasters, aggregateExtent);
            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);

            // Discover multidimensional coverage metadata (cubes) registered for the
            // layer so the service advertises hasMultidimensions and inline info that
            // matches the dedicated /multidimensionalInfo operation.
            var multidimensionalInfo = await _multidimensionalInfoBuilder
                .BuildAsync(layerId, cancellationToken)
                .ConfigureAwait(false);

            // Get raster statistics for the layer mosaic.
            var statistics = rasters.Length == 1
                ? await _rasterStore.GetStatisticsAsync(layerId, referenceRaster.Id, cancellationToken: cancellationToken)
                : await _rasterStore.GetMosaicStatisticsAsync(
                    layerId,
                    rasters.Select(r => r.Id).ToArray(),
                    mergeStrategy,
                    cancellationToken: cancellationToken);

            // Get raster extent
            var extent = aggregateExtent;
            if (extent == null)
            {
                ImageServerLog.ExtentNotAvailable(_logger, layerId);
                return StandardErrorHelpers.CreateInternalServerError(context, "Unable to determine raster extent.");
            }

            // Build service info response
            var serviceInfo = new ImageServerServiceInfo
            {
                CurrentVersion = ArcGisVersion,
                ServiceDescription = resolved.Description ?? $"Image service for {resolved.DisplayName}",
                Name = resolved.DisplayName,
                Description = resolved.Description,
                Extent = new ImageServerExtent
                {
                    XMin = extent.Value.XMin,
                    YMin = extent.Value.YMin,
                    XMax = extent.Value.XMax,
                    YMax = extent.Value.YMax,
                    SpatialReference = CreateSpatialReference(extent.Value.Srid)
                },
                SpatialReference = CreateSpatialReference(referenceRaster.Srid),
                PixelSizeX = CalculatePixelSize(extent.Value, referenceRaster.Width),
                PixelSizeY = CalculatePixelSize(extent.Value, referenceRaster.Height, isHeight: true),
                BandCount = referenceRaster.BandCount,
                PixelType = MapPixelType(referenceRaster.PixelType),
                MinPixelSize = MinPixelSize,
                MaxPixelSize = MaxPixelSize,
                CopyrightText = resolved.Description ?? "",
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
                TimeInfo = BuildTimeInfo(ImageServerV2Lookups.ReadTimeFieldHints(resolved.Resource), rasters),
                HasMultidimensions = multidimensionalInfo is { Variables.Length: > 0 },
                MultidimensionalInfo = multidimensionalInfo is { Variables.Length: > 0 } ? multidimensionalInfo : null
            };

            ImageServerLog.ServiceInfoGenerated(_logger, layerId, referenceRaster.BandCount, statistics.Length);

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
    /// Builds the Esri-conformant <c>timeInfo</c> block when the V2 resource declares
    /// temporal fields or when the raster catalog carries acquisition timestamps.
    /// </summary>
    private static ImageServerTimeInfo? BuildTimeInfo(
        (string? StartTimeField, string? EndTimeField, string? TrackIdField) timeHints,
        IReadOnlyList<RasterInfo> rasters)
    {
        var hasAcquisitionDates = rasters.Any(r => r.AcquisitionDate.HasValue);
        var timeExtent = hasAcquisitionDates ? ImageServerMosaicHelpers.CreateTimeExtent(rasters) : null;
        var hasDeclaredFields = !string.IsNullOrWhiteSpace(timeHints.StartTimeField) ||
                                !string.IsNullOrWhiteSpace(timeHints.EndTimeField) ||
                                !string.IsNullOrWhiteSpace(timeHints.TrackIdField);
        if (!hasDeclaredFields && timeExtent is null)
        {
            return null;
        }

        return new ImageServerTimeInfo
        {
            StartTimeField = timeHints.StartTimeField ?? (timeExtent is null ? null : "AcquisitionDate"),
            EndTimeField = timeHints.EndTimeField,
            TrackIdField = timeHints.TrackIdField,
            TimeExtent = timeExtent,
            TimeReference = new ImageServerTimeReference()
        };
    }

    private static RasterInfo CreateMosaicReferenceRaster(
        RasterInfo[] rasters,
        RasterExtent? aggregateExtent)
    {
        var primary = rasters[0];
        if (aggregateExtent is not { } extent)
        {
            return primary;
        }

        var pixelWidth = rasters
            .Select(r => r.GeoTransform is { Length: >= 2 } ? Math.Abs(r.GeoTransform[1]) : 0d)
            .Where(v => v > 0)
            .DefaultIfEmpty(0d)
            .Min();
        var pixelHeight = rasters
            .Select(r => r.GeoTransform is { Length: >= 6 } ? Math.Abs(r.GeoTransform[5]) : 0d)
            .Where(v => v > 0)
            .DefaultIfEmpty(0d)
            .Min();

        var width = pixelWidth > 0
            ? Math.Max(1, (int)Math.Ceiling((extent.XMax - extent.XMin) / pixelWidth))
            : primary.Width;
        var height = pixelHeight > 0
            ? Math.Max(1, (int)Math.Ceiling((extent.YMax - extent.YMin) / pixelHeight))
            : primary.Height;

        return primary with
        {
            Width = width,
            Height = height,
            Srid = extent.Srid ?? primary.Srid,
            Extent = extent
        };
    }

}
