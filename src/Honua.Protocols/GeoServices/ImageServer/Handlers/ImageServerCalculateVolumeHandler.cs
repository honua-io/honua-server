// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

// Cut and fill are integrated from the associated DEM, and a missing DEM or an area over the pixel budget returns 501 or 400 rather than a fabricated volume.
internal sealed class ImageServerCalculateVolumeHandler
{
    private const string NoDemMessage =
        "Volume calculation requires a DEM/elevation surface (raster_sensor_metadata.dem_source) " +
        "that is not modeled for this raster.";

    private readonly IRasterStore _rasterStore;
    private readonly ImageServerCalculateVolumeOptions _options;
    private readonly ILogger<ImageServerCalculateVolumeHandler> _logger;

    public ImageServerCalculateVolumeHandler(
        IRasterStore rasterStore,
        IOptions<ImageServerCalculateVolumeOptions> options,
        ILogger<ImageServerCalculateVolumeHandler> logger)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> CalculateVolumeAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "calculateVolume",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "calculateVolume")
            .WithTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

        try
        {
            var primary = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (primary is not { } raster || raster.Id <= 0)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // baseType selects the reference surface. Only the constant-z plane (baseType 0) is
            // supported: the per-pixel band vectors carry no positions, so a best-fitting plane or a
            // perimeter-derived base (baseType 1-4) cannot be reconstructed honestly here.
            if (!TryParseBaseType(ImageServerRequestValues.GetString(values, "baseType"), out var baseType, out var baseTypeError))
            {
                ImageServerLog.InvalidCalculateVolumeParameters(_logger, layerId, baseTypeError!);
                return StandardErrorHelpers.CreateBadRequest(context, baseTypeError!);
            }

            if (baseType != 0)
            {
                return StandardErrorHelpers.CreateNotImplemented(
                    context,
                    "Only baseType=0 (constant elevation plane, via constantZ) is supported; " +
                    "best-fitting-plane and perimeter-derived base surfaces are not implemented.");
            }

            if (!TryParseConstantZ(ImageServerRequestValues.GetString(values, "constantZ") ?? ImageServerRequestValues.GetString(values, "referenceHeight"),
                    out var basePlane, out var constantZError))
            {
                ImageServerLog.InvalidCalculateVolumeParameters(_logger, layerId, constantZError!);
                return StandardErrorHelpers.CreateBadRequest(context, constantZError!);
            }

            if (!ImageServerVolumeGeometries.TryParse(
                    ImageServerRequestValues.GetString(values, "geometries"),
                    ImageServerRequestValues.GetString(values, "geometryType"),
                    raster.Srid,
                    out var areas,
                    out var geometriesError))
            {
                ImageServerLog.InvalidCalculateVolumeParameters(_logger, layerId, geometriesError!);
                return StandardErrorHelpers.CreateBadRequest(context, geometriesError!);
            }

            if (areas.Count > _options.MaxGeometries)
            {
                var tooMany = $"calculateVolume accepts at most {_options.MaxGeometries} geometries; received {areas.Count}.";
                ImageServerLog.InvalidCalculateVolumeParameters(_logger, layerId, tooMany);
                return StandardErrorHelpers.CreateBadRequest(context, tooMany);
            }

            // Resolve the DEM surface from the primary raster's sensor metadata. Same contract as the
            // DEM-height mensuration path: a missing or non-layer-id dem_source is honest 501.
            var metadata = await _rasterStore.GetSensorMetadataAsync([raster.Id], cancellationToken).ConfigureAwait(false);
            var sensor = metadata.TryGetValue(raster.Id, out var meta) ? meta : raster.SensorMetadata;
            var demSource = sensor?.DemSource;
            if (string.IsNullOrWhiteSpace(demSource) ||
                !int.TryParse(demSource, NumberStyles.Integer, CultureInfo.InvariantCulture, out var demLayerId))
            {
                return StandardErrorHelpers.CreateNotImplemented(context, NoDemMessage);
            }

            var demPrimary = await _rasterStore.GetPrimaryRasterInfoAsync(demLayerId, cancellationToken).ConfigureAwait(false);
            if (demPrimary is not { } demRaster || demRaster.Id <= 0 || demRaster.GeoTransform is not { Length: 6 } geoTransform)
            {
                return StandardErrorHelpers.CreateNotImplemented(context, NoDemMessage);
            }

            // Pixel ground area = |det| of the geotransform's linear part (handles rotation/skew),
            // converted from the DEM CRS's native units (degrees, Web Mercator meters, feet) to ground square meters.
            var nativePixelArea = Math.Abs((geoTransform[1] * geoTransform[5]) - (geoTransform[2] * geoTransform[4]));
            if (nativePixelArea <= 0d || double.IsNaN(nativePixelArea) || double.IsInfinity(nativePixelArea))
            {
                return StandardErrorHelpers.CreateNotImplemented(context, NoDemMessage);
            }

            var pixelArea = await ResolveGroundPixelAreaSquareMetersAsync(
                context, demRaster, geoTransform, nativePixelArea, cancellationToken).ConfigureAwait(false);
            if (pixelArea <= 0d || double.IsNaN(pixelArea) || double.IsInfinity(pixelArea))
            {
                return StandardErrorHelpers.CreateNotImplemented(context, NoDemMessage);
            }

            var demRasters = await _rasterStore.QueryRastersAsync(
                demLayerId, new RasterSelectionQuery(), cancellationToken).ConfigureAwait(false);
            if (demRasters.Length == 0)
            {
                return StandardErrorHelpers.CreateNotImplemented(context, NoDemMessage);
            }

            var rasterIds = demRasters.Select(r => r.Id).ToArray();
            var results = new List<CalculateVolumeResult>(areas.Count);
            foreach (var aoi in areas)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var vectors = await _rasterStore.ReadClippedBandVectorsAsync(
                    demLayerId,
                    rasterIds,
                    RasterMergeStrategy.Newest,
                    aoi.ClipGeometry,
                    aoi.ClipSrid,
                    [1],
                    _options.MaxPixelsPerGeometry,
                    cancellationToken).ConfigureAwait(false);

                if (vectors.ExceededPixelBudget)
                {
                    var tooLarge =
                        $"An area-of-interest spans {vectors.BoundingPixelCount} DEM pixels, exceeding the " +
                        $"synchronous volume budget of {_options.MaxPixelsPerGeometry}. Reduce the AOI size.";
                    ImageServerLog.InvalidCalculateVolumeParameters(_logger, layerId, tooLarge);
                    return StandardErrorHelpers.CreateBadRequest(context, tooLarge);
                }

                results.Add(Integrate(vectors.Pixels, basePlane, pixelArea));
            }

            var response = new CalculateVolumeResponse { Results = results };
            ImageServerLog.CalculateVolumeCompleted(_logger, layerId, results.Count);
            scope.SetSuccess(results.Count);
            return Results.Json(response, ImageServerJsonContext.Default.CalculateVolumeResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any unexpected
        // failure must map to a generic 500 rather than crash the host or leak internals.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.CalculateVolumeFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "ImageServer calculateVolume failed.");
        }
    }

    private static CalculateVolumeResult Integrate(IReadOnlyList<double[]> pixels, double basePlane, double pixelArea)
    {
        if (pixels.Count == 0)
        {
            return new CalculateVolumeResult { Area = 0d, Cut = 0d, Fill = 0d, MinZ = 0d, MaxZ = 0d, MeanZ = 0d };
        }

        var cutSum = 0d;
        var fillSum = 0d;
        var elevationSum = 0d;
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var elevation in (pixels).Select(pixel => pixel[0]))
        {
            var delta = elevation - basePlane;
            if (delta > 0d)
            {
                cutSum += delta;
            }
            else if (delta < 0d)
            {
                fillSum += delta;
            }

            elevationSum += elevation;
            min = Math.Min(min, elevation);
            max = Math.Max(max, elevation);
        }

        return new CalculateVolumeResult
        {
            Area = pixels.Count * pixelArea,
            Cut = cutSum * pixelArea,
            Fill = fillSum * pixelArea,
            MinZ = min,
            MaxZ = max,
            MeanZ = elevationSum / pixels.Count,
        };
    }

    private static async ValueTask<double> ResolveGroundPixelAreaSquareMetersAsync(
        HttpContext context,
        RasterInfo demRaster,
        double[] geoTransform,
        double nativePixelArea,
        CancellationToken cancellationToken)
    {
        var centerY = demRaster.Extent is { } extent
            ? (extent.YMin + extent.YMax) * 0.5d
            : geoTransform[3] + (demRaster.Width * 0.5d * geoTransform[4]) + (demRaster.Height * 0.5d * geoTransform[5]);

        if (demRaster.Srid is not int srid)
        {
            return nativePixelArea;
        }

        var classifier = context.RequestServices.GetService<IGeographicSridClassifier>();
        var crsRegistry = context.RequestServices.GetService<ICrsRegistry>();
        var crsDefinition = crsRegistry is not null
            ? await crsRegistry.ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false)
            : null;

        bool isGeographic;
        if (classifier is not null)
        {
            isGeographic = await classifier.IsGeographicForMeasurementAsync(srid, cancellationToken).ConfigureAwait(false);
        }
        else if (crsDefinition.HasValue)
        {
            isGeographic = crsDefinition.Value.IsGeographic;
        }
        else
        {
            isGeographic = ImageServerMensurationMath.IsGeographicSrid(srid);
        }

        var linearMetersPerUnit = 1d;
        if (!isGeographic && SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid) != 3857)
        {
            if (classifier is not null &&
                await classifier.TryResolveLinearUnitFactorAsync(srid, cancellationToken).ConfigureAwait(false) is double classifiedFactor)
            {
                linearMetersPerUnit = classifiedFactor;
            }
            else if (crsDefinition?.LinearUnitFactor is double defFactor && defFactor > 0d && double.IsFinite(defFactor))
            {
                linearMetersPerUnit = defFactor;
            }
            else if (!string.IsNullOrWhiteSpace(crsDefinition?.Wkt))
            {
                linearMetersPerUnit = CrsLinearUnitFactor.Resolve(proj4Text: null, crsDefinition.Value.Wkt, isGeographic: false);
            }
            else
            {
                linearMetersPerUnit = srid is 2992 or 2994 or 2273
                    ? CrsLinearUnitFactor.InternationalFootMeters
                    : CoordinateTransformer.LinearUnitToMeters(srid);
            }
        }

        return ImageServerMensurationMath.ComputeGroundPixelAreaSquareMeters(
            nativePixelArea,
            centerY,
            srid,
            isGeographic,
            linearMetersPerUnit);
    }

    private static bool TryParseBaseType(string? raw, out int baseType, out string? error)
    {
        baseType = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out baseType) ||
            baseType is < 0 or > 4)
        {
            error = "baseType must be an integer between 0 and 4.";
            return false;
        }

        return true;
    }

    private static bool TryParseConstantZ(string? raw, out double constantZ, out string? error)
    {
        constantZ = 0d;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "constantZ (the base elevation plane) is required for baseType=0.";
            return false;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out constantZ) ||
            double.IsNaN(constantZ) || double.IsInfinity(constantZ))
        {
            error = "constantZ must be a finite number.";
            return false;
        }

        return true;
    }
}
