// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the Image Server <c>keyProperties</c> operation. Returns raster key
/// properties (band properties, data type, band count, NoData) sourced from the shared
/// raster store metadata for the layer's primary raster.
/// </summary>
internal sealed class ImageServerKeyPropertiesHandler
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerKeyPropertiesHandler> _logger;

    public ImageServerKeyPropertiesHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<ImageServerKeyPropertiesHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns key properties metadata for the layer's primary raster.
    /// </summary>
    public async Task<IResult> GetKeyPropertiesAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "key-properties",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "key-properties");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            var primary = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);
            if (primary is not { } raster)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            var bandProperties = new BandProperty[raster.BandCount];
            for (var band = 0; band < raster.BandCount; band++)
            {
                bandProperties[band] = new BandProperty
                {
                    BandName = $"Band_{band + 1}",
                    PixelType = raster.PixelType,
                };
            }

            var (lowCellSize, highCellSize) = ComputeCellSizes(raster.GeoTransform);

            var response = new KeyPropertiesResponse
            {
                BandProperties = bandProperties,
                LowCellSize = lowCellSize,
                HighCellSize = highCellSize,
                MaxCellSize = highCellSize,
                ConfigKeyword = string.Empty,
                BandDefinitionKeyword = string.Empty,
                DataType = MapPixelType(raster.PixelType),
                BandCount = raster.BandCount,
                NoDataValue = raster.NoDataValue,
            };

            ImageServerLog.ServiceInfoGenerated(_logger, layerId, raster.BandCount, raster.BandCount);
            scope.SetSuccess(raster.BandCount);

            return Results.Json(response, ImageServerJsonContext.Default.KeyPropertiesResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.ServiceInfoFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving key properties.");
        }
    }

    /// <summary>
    /// Derives the finest/coarsest cell size from the raster geotransform.
    /// GeoTransform layout: [upperLeftX, scaleX, skewX, upperLeftY, skewY, scaleY];
    /// pixel width is <c>|scaleX|</c> (index 1) and pixel height is <c>|scaleY|</c> (index 5).
    /// Returns <c>(null, null)</c> when no geotransform is available.
    /// </summary>
    private static (double? Low, double? High) ComputeCellSizes(double[]? geoTransform)
    {
        if (geoTransform is not { Length: >= 6 })
        {
            return (null, null);
        }

        var pixelWidth = Math.Abs(geoTransform[1]);
        var pixelHeight = Math.Abs(geoTransform[5]);

        if (pixelWidth <= 0 && pixelHeight <= 0)
        {
            return (null, null);
        }

        if (pixelWidth <= 0)
        {
            return (pixelHeight, pixelHeight);
        }

        if (pixelHeight <= 0)
        {
            return (pixelWidth, pixelWidth);
        }

        return (Math.Min(pixelWidth, pixelHeight), Math.Max(pixelWidth, pixelHeight));
    }

    private static string MapPixelType(string postgisPixelType)
        => postgisPixelType.ToUpperInvariant() switch
        {
            "8BUI" => "U8",
            "8BSI" => "S8",
            "16BUI" => "U16",
            "16BSI" => "S16",
            "32BUI" => "U32",
            "32BSI" => "S32",
            "32BF" => "F32",
            "64BF" => "F64",
            _ => "U8",
        };
}
