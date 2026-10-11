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
            if (ImageServerV2Lookups.FindByStorageLayerId(snapshot, layerId, context) is null)
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

            var response = BuildResponse(raster);

            ImageServerLog.ServiceInfoGenerated(_logger, layerId, raster.BandCount, raster.BandCount);
            scope.SetSuccess(raster.BandCount);

            return Results.Json(response, ImageServerJsonContext.Default.KeyPropertiesResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is the top-level request handler boundary; any
        // unanticipated failure must map to a generic 500 rather than crash the request.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.ServiceInfoFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving key properties.");
        }
    }

    internal static KeyPropertiesResponse BuildResponse(Core.Features.Raster.Domain.RasterInfo raster)
    {
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
        return new KeyPropertiesResponse
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
    }

    /// <summary>
    /// Derives the finest/coarsest cell size from the raster geotransform.
    /// GeoTransform layout: [upperLeftX, scaleX, skewX, upperLeftY, skewY, scaleY];
    /// pixel sizes are the lengths of the column (scaleX, skewY) and row (skewX, scaleY) vectors.
    /// Returns <c>(null, null)</c> when no geotransform is available.
    /// </summary>
    private static (double? Low, double? High) ComputeCellSizes(double[]? geoTransform)
    {
        if (geoTransform is not { Length: >= 6 })
        {
            return (null, null);
        }

        var pixelWidth = VectorLength(geoTransform[1], geoTransform[4]);
        var pixelHeight = VectorLength(geoTransform[2], geoTransform[5]);

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

    internal static (double X, double Y) ComputePixelSizes(Core.Features.Raster.Domain.RasterInfo raster)
    {
        if (raster.GeoTransform is { Length: >= 6 } transform)
        {
            return (VectorLength(transform[1], transform[4]), VectorLength(transform[2], transform[5]));
        }

        return raster.Extent is { } extent
            ? (raster.Width > 0 ? (extent.XMax - extent.XMin) / raster.Width : 0,
               raster.Height > 0 ? (extent.YMax - extent.YMin) / raster.Height : 0)
            : (0, 0);
    }

    private static double VectorLength(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return 0;
        }

        // Scale before squaring so a finite affine coefficient does not overflow.
        var scale = Math.Max(Math.Abs(x), Math.Abs(y));
        return scale == 0 ? 0 : scale * Math.Sqrt(Math.Pow(x / scale, 2) + Math.Pow(y / scale, 2));
    }

    internal static string MapPixelType(string postgisPixelType)
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
