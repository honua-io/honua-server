// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Thin ImageServer adapter for metadata children belonging to one canonical raster item.
/// </summary>
internal sealed class ImageServerRasterItemHandler
{
    private const int DefaultBinCount = 256;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerRasterItemHandler> _logger;

    public ImageServerRasterItemHandler(
        IRasterStore rasterStore,
        ILogger<ImageServerRasterItemHandler> logger)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IResult> GetInfoAsync(HttpContext context, int layerId, long rasterId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, rasterId, "raster-item-info", raster =>
        {
            var transform = raster.GeoTransform;
            var spatialReference = raster.Srid is { } srid
                ? new SpatialReference { Wkid = srid, LatestWkid = srid }
                : null;
            var response = new RasterItemInfoResponse
            {
                Origin = transform is { Length: >= 6 }
                    ? new Point { X = transform[0], Y = transform[3] }
                    : null,
                BlockWidth = raster.Width,
                BlockHeight = raster.Height,
                PixelSizeX = transform is { Length: >= 6 } ? Math.Abs(transform[1]) : null,
                PixelSizeY = transform is { Length: >= 6 } ? Math.Abs(transform[5]) : null,
                Extent = BuildExtent(raster.Extent, spatialReference),
                BandCount = raster.BandCount,
                PixelType = ImageServerKeyPropertiesHandler.MapPixelType(raster.PixelType),
            };
            return Task.FromResult<(IResult Result, int Count)>((
                Results.Json(response, ImageServerJsonContext.Default.RasterItemInfoResponse), 1));
        }, cancellationToken);

    public Task<IResult> GetKeyPropertiesAsync(HttpContext context, int layerId, long rasterId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, rasterId, "raster-item-key-properties", raster =>
            Task.FromResult<(IResult Result, int Count)>((Results.Json(
                ImageServerKeyPropertiesHandler.BuildResponse(raster),
                ImageServerJsonContext.Default.KeyPropertiesResponse), raster.BandCount)), cancellationToken);

    public Task<IResult> GetHistogramsAsync(HttpContext context, int layerId, long rasterId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, rasterId, "raster-item-histograms", async raster =>
        {
            var histograms = await _rasterStore.GetHistogramsAsync(
                layerId, raster.Id, bands: null, DefaultBinCount, cancellationToken: cancellationToken);
            var entries = histograms.Select(static histogram => new BandHistogram
            {
                Size = histogram.BinCount,
                Min = histogram.Min,
                Max = histogram.Max,
                Counts = histogram.Counts,
            }).ToArray();
            return (Results.Json(new HistogramsResourceResponse { Histograms = entries },
                ImageServerJsonContext.Default.HistogramsResourceResponse), entries.Length);
        }, cancellationToken);

    private async Task<IResult> ExecuteAsync(
        HttpContext context,
        int layerId,
        long rasterId,
        string operation,
        Func<RasterInfo, Task<(IResult Result, int Count)>> execute,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            operation,
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, operation);
        scope.WithTag("honua.raster.id", rasterId);

        try
        {
            var raster = await _rasterStore.GetRasterInfoAsync(layerId, rasterId, cancellationToken);
            if (raster is not { } found)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Raster item not found.");
            }

            var result = await execute(found);
            scope.SetSuccess(result.Count);
            return result.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.ServiceInfoFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving raster item metadata.");
        }
    }

    private static ImageServerExtent? BuildExtent(RasterExtent? extent, SpatialReference? fallbackSpatialReference)
        => extent is not { } value || (value.Srid is null && fallbackSpatialReference is null)
            ? null
            : new ImageServerExtent
            {
                XMin = value.XMin,
                YMin = value.YMin,
                XMax = value.XMax,
                YMax = value.YMax,
                SpatialReference = value.Srid is { } srid
                    ? new SpatialReference { Wkid = srid, LatestWkid = srid }
                    : fallbackSpatialReference!,
            };
}
