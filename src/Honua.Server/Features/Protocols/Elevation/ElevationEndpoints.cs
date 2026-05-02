// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Raster;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Elevation;

internal static class ElevationEndpoints
{
    private const string JsonContentType = "application/json";
    private const int DefaultSrid = 4326;

    public static IEndpointRouteBuilder MapElevationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/elevation/{datasetId}/value", HandleGetValue)
            .WithDisplayName("Get Elevation At Point")
            .WithName("GetElevationValue")
            .WithSummary("Sample elevation at a single coordinate")
            .WithDescription("Returns numeric elevation, source metadata, and no-data status for a single coordinate against a registered raster dataset")
            .WithTags("Elevation")
            .Produces<ElevationValueResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet("/elevation/{datasetId}/profile", HandleGetProfile)
            .WithDisplayName("Get Elevation Profile")
            .WithName("GetElevationProfile")
            .WithSummary("Sample elevation along a line geometry")
            .WithDescription("Returns ordered distance/elevation samples for a WKT LineString against a registered raster dataset")
            .WithTags("Elevation")
            .Produces<ElevationProfileResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> HandleGetValue(
        string datasetId,
        [FromQuery] double? x,
        [FromQuery] double? y,
        [FromQuery] string? srid,
        [FromQuery] string? mosaicRule,
        HttpContext context,
        [FromServices] IElevationService elevationService,
        [FromServices] ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        if (!x.HasValue || !y.HasValue)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Both 'x' and 'y' query parameters are required.");
        }

        if (!IsFiniteCoordinate(x.Value) || !IsFiniteCoordinate(y.Value))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Coordinates 'x' and 'y' must be finite numeric values.");
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var resolvedSrid = await ResolveSridAsync(crsRegistry, srid, cancellationToken);
        if (!resolvedSrid.IsSupported)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"CRS '{srid}' is not supported by the spatial reference registry.");
        }

        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata, mosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.value");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.value");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await elevationService.QueryPointAsync(
                layer.Id,
                x.Value,
                y.Value,
                resolvedSrid.Srid,
                mergeStrategy,
                cancellationToken);

            activity?.SetTag("honua.elevation.raster_count", result.RasterIds.Length);
            activity?.SetTag("honua.elevation.no_data", result.NoData);

            var response = BuildValueResponse(datasetId, result, mergeStrategy);
            return Results.Json(response, ElevationJsonContext.Default.ElevationValueResponse, contentType: JsonContentType);
        }
        catch (ElevationQueryException ex)
        {
            HonuaTelemetry.RecordException(activity, ex);
            return MapElevationException(context, ex);
        }
    }

    private static async Task<IResult> HandleGetProfile(
        string datasetId,
        [FromQuery] string? line,
        [FromQuery] int? sampleCount,
        [FromQuery] double? interval,
        [FromQuery] string? srid,
        [FromQuery] string? mosaicRule,
        HttpContext context,
        [FromServices] IElevationService elevationService,
        [FromServices] ICrsRegistry crsRegistry,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Query parameter 'line' is required and must contain a WKT LineString.");
        }

        if (!TryParseLineString(line, out var lineString, out var parseError))
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"Invalid line geometry: {parseError}");
        }

        var elevationLimits = limitsOptions.Value.Elevation;

        if (sampleCount.HasValue && sampleCount.Value < 2)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Query parameter 'sampleCount' must be at least 2.");
        }

        if (sampleCount.HasValue && sampleCount.Value > elevationLimits.MaxSampleCount)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"Query parameter 'sampleCount' ({sampleCount.Value}) exceeds the configured maximum of {elevationLimits.MaxSampleCount}.");
        }

        if (interval.HasValue && (!double.IsFinite(interval.Value) || interval.Value <= 0))
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Query parameter 'interval' must be a positive finite value in meters.");
        }

        if (interval.HasValue && interval.Value < elevationLimits.MinIntervalMeters)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"Query parameter 'interval' must be at least {elevationLimits.MinIntervalMeters.ToString(CultureInfo.InvariantCulture)} meters.");
        }

        if (interval.HasValue && interval.Value > elevationLimits.MaxIntervalMeters)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"Query parameter 'interval' ({interval.Value.ToString(CultureInfo.InvariantCulture)}) exceeds the configured maximum of {elevationLimits.MaxIntervalMeters.ToString(CultureInfo.InvariantCulture)} meters.");
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var resolvedSrid = await ResolveSridAsync(crsRegistry, srid, cancellationToken);
        if (!resolvedSrid.IsSupported)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"CRS '{srid}' is not supported by the spatial reference registry.");
        }

        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata, mosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.profile");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.profile");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var lineSrid = resolvedSrid.Srid ?? DefaultSrid;
            var lineWkb = WriteLineWkb(lineString);
            var resolvedSampleCount = ResolveSampleCount(
                sampleCount,
                interval,
                elevationLimits);

            activity?.SetTag("honua.elevation.sample_count", resolvedSampleCount);

            var result = await elevationService.QueryProfileAsync(
                layer.Id,
                lineWkb,
                lineSrid,
                new ProfileSamplingOptions { SampleCount = resolvedSampleCount },
                mergeStrategy,
                cancellationToken);

            activity?.SetTag("honua.elevation.raster_count", result.RasterIds.Length);
            activity?.SetTag("honua.elevation.line_length_m", result.LineLengthMeters);
            activity?.SetTag("honua.elevation.no_data", result.IsAllNoData);

            var response = BuildProfileResponse(datasetId, layer.Id, lineSrid, result, mergeStrategy);
            return Results.Json(response, ElevationJsonContext.Default.ElevationProfileResponse, contentType: JsonContentType);
        }
        catch (ElevationQueryException ex)
        {
            HonuaTelemetry.RecordException(activity, ex);
            return MapElevationException(context, ex);
        }
    }

    private static int ResolveSampleCount(
        int? requestedSampleCount,
        double? requestedInterval,
        ElevationLimits limits)
    {
        if (requestedSampleCount.HasValue)
        {
            return Math.Min(requestedSampleCount.Value, limits.MaxSampleCount);
        }

        if (requestedInterval.HasValue)
        {
            // Geodesic line length is unknown without an extra round-trip; cap at MaxSampleCount.
            return Math.Clamp(limits.MaxSampleCount, 2, limits.MaxSampleCount);
        }

        return Math.Clamp(limits.DefaultSampleCount, 2, limits.MaxSampleCount);
    }

    private static byte[] WriteLineWkb(LineString lineString)
    {
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
        return writer.Write(lineString);
    }

    private static bool TryParseLineString(string wkt, out LineString lineString, out string error)
    {
        lineString = null!;
        error = string.Empty;

        try
        {
            var reader = new WKTReader();
            var geometry = reader.Read(wkt);
            if (geometry is null)
            {
                error = "WKT could not be parsed.";
                return false;
            }

            if (geometry is not LineString parsedLine)
            {
                error = $"Geometry type '{geometry.GeometryType}' is not supported. Only LineString is allowed.";
                return false;
            }

            if (parsedLine.NumPoints < 2)
            {
                error = "LineString must contain at least two coordinates.";
                return false;
            }

            foreach (var coordinate in parsedLine.Coordinates)
            {
                if (!IsFiniteCoordinate(coordinate.X) || !IsFiniteCoordinate(coordinate.Y))
                {
                    error = "LineString coordinates must be finite numeric values.";
                    return false;
                }
            }

            lineString = parsedLine;
            return true;
        }
        catch (ParseException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task<SridResolutionResult> ResolveSridAsync(
        ICrsRegistry crsRegistry,
        string? sridParameter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sridParameter))
        {
            return SridResolutionResult.Default;
        }

        if (!int.TryParse(sridParameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) || srid <= 0)
        {
            return SridResolutionResult.Unsupported;
        }

        var supported = await crsRegistry.IsSridSupportedAsync(srid, cancellationToken);
        return supported ? new SridResolutionResult(srid, true) : SridResolutionResult.Unsupported;
    }

    private static Task<LayerValidationHelpers.LayerValidationResult> ValidateDatasetAsync(
        HttpContext context,
        string datasetId,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(datasetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId,
                AccessScope.Read,
                ServiceProtocols.Elevation,
                cancellationToken);
        }

        return LayerValidationHelpers.ValidateCollectionWithAccessAsync(
            context,
            datasetId,
            AccessScope.Read,
            ServiceProtocols.Elevation,
            cancellationToken);
    }

    private static IResult MapElevationException(HttpContext context, ElevationQueryException exception)
        => exception.FailureKind switch
        {
            ElevationFailureKind.SourceUnavailable => StandardErrorHelpers.CreateNotFound(context, exception.Message),
            ElevationFailureKind.UnsupportedCrs => StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            ElevationFailureKind.InvalidGeometry => StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            _ => StandardErrorHelpers.CreateBadRequest(context, exception.Message)
        };

    private static ElevationValueResponse BuildValueResponse(
        string datasetId,
        ElevationPointResult result,
        RasterMergeStrategy mergeStrategy)
    {
        return new ElevationValueResponse
        {
            DatasetId = datasetId,
            LayerId = result.LayerId,
            Elevation = result.Elevation,
            NoData = result.NoData,
            OutOfBounds = result.OutOfBounds,
            X = result.X,
            Y = result.Y,
            QuerySrid = result.QuerySrid,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            Source = BuildSourceMetadata(
                result.RasterIds,
                result.SourceSrid,
                result.PixelType,
                result.NoDataValue,
                result.VerticalUnit,
                result.VerticalDatum)
        };
    }

    private static ElevationProfileResponse BuildProfileResponse(
        string datasetId,
        int layerId,
        int lineSrid,
        ElevationProfileResult result,
        RasterMergeStrategy mergeStrategy)
    {
        var samples = new ElevationProfileSample[result.Samples.Length];
        for (var i = 0; i < result.Samples.Length; i++)
        {
            var sample = result.Samples[i];
            samples[i] = new ElevationProfileSample
            {
                DistanceMeters = sample.DistanceMeters,
                Elevation = sample.Elevation,
                NoData = sample.NoData
            };
        }

        return new ElevationProfileResponse
        {
            DatasetId = datasetId,
            LayerId = layerId,
            SampleCount = result.SampleCount,
            LineLengthMeters = result.LineLengthMeters,
            LineSrid = lineSrid,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            IsAllNoData = result.IsAllNoData,
            Samples = samples,
            Source = BuildSourceMetadata(
                result.RasterIds,
                result.SourceSrid,
                result.PixelType,
                result.NoDataValue,
                result.VerticalUnit,
                result.VerticalDatum)
        };
    }

    private static ElevationSourceMetadata BuildSourceMetadata(
        long[] rasterIds,
        int? sourceSrid,
        string? pixelType,
        double? noDataValue,
        string? verticalUnit,
        string? verticalDatum)
    {
        return new ElevationSourceMetadata
        {
            RasterIds = rasterIds,
            RasterCount = rasterIds.Length,
            SourceSrid = sourceSrid,
            SourceCrs = sourceSrid.HasValue && sourceSrid.Value > 0
                ? string.Create(CultureInfo.InvariantCulture, $"EPSG:{sourceSrid.Value}")
                : null,
            PixelType = pixelType,
            NoDataValue = noDataValue,
            VerticalUnit = verticalUnit,
            VerticalDatum = verticalDatum
        };
    }

    private static string FormatMergeStrategy(RasterMergeStrategy strategy) => strategy switch
    {
        RasterMergeStrategy.Newest => "newest",
        RasterMergeStrategy.Oldest => "oldest",
        RasterMergeStrategy.Average => "average",
        RasterMergeStrategy.Max => "max",
        RasterMergeStrategy.Min => "min",
        _ => "newest"
    };

    private static bool IsFiniteCoordinate(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);

    private readonly record struct SridResolutionResult(int? Srid, bool IsSupported)
    {
        public static SridResolutionResult Default => new(null, true);

        public static SridResolutionResult Unsupported => new(null, false);
    }
}
