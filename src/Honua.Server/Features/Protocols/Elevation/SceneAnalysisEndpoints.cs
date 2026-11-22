// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Raster;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Honua.Core.Features.Raster;

namespace Honua.Server.Features.Protocols.Elevation;

// Routes are anonymous so layer permissions can decide access; each handler checks layer authorization and the license before reading elevation.
internal static partial class SceneAnalysisEndpoints
{
    private const string JsonContentType = "application/json";
    private const string SunShadowEntitlement = "analytics.sun-shadow";
    private const string SliceEntitlement = "analytics.slice";

    private const double CoordinateEqualityEpsilon = 1e-9;

    public static IEndpointRouteBuilder MapSceneAnalysisEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/elevation/{datasetId}/sun-shadow", HandleSunShadow)
            .WithDisplayName("Compute Sun/Shadow")
            .WithName("ComputeSunShadow")
            .WithSummary("Compute the solar position and cast shadow extent over the elevation surface")
            .WithDescription("Computes the solar altitude/azimuth for a UTC instant and location, then casts the shadow against the elevation surface in the anti-solar direction")
            .WithTags("Elevation")
            .Accepts<SunShadowRequest>(JsonContentType)
            .Produces<SunShadowResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status402PaymentRequired)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .AllowAnonymous();

        endpoints.MapPost("/elevation/{datasetId}/slice", HandleSlice)
            .WithDisplayName("Compute Slice Cross-Section")
            .WithName("ComputeSlice")
            .WithSummary("Compute a slice plane's intersection with the elevation surface")
            .WithDescription("Samples the elevation surface along a vertical slice plane and returns the intersection profile with min/max/length metadata")
            .WithTags("Elevation")
            .Accepts<SliceRequest>(JsonContentType)
            .Produces<SliceResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status402PaymentRequired)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleSunShadow(
        string datasetId,
        HttpContext context,
        SceneAnalysisService sceneService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Honua.Elevation.SceneAnalysis");

        var gate = LicenseGate.RequireEntitlement(context, SunShadowEntitlement, "Sun/Shadow Analysis", logger);
        if (gate is not null)
        {
            return gate;
        }

        SunShadowRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                SceneAnalysisJsonContext.Default.SunShadowRequest,
                cancellationToken);
        }
        // Intentional broad catch: request-body deserialization can fail for many reasons
        // (malformed JSON, unsupported content-type, client disconnect mid-read); all map
        // uniformly to a 400 bad-request response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Warning))
            {
                var sanitizedDatasetId = LogValueRedactor.SanitizeForLog(datasetId);
                LogSunShadowRequestParseFailed(logger, sanitizedDatasetId, ex);
            }
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON.");
        }

        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body is required.");
        }

        var (observerLonValid, observerLon, observerLat) = ElevationAnalysisValidation.ResolveCoordinate(request.ObserverLon, request.ObserverLat);
        if (!observerLonValid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Observer 'observerLon' and 'observerLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        if (request.Timestamp is not { } timestamp)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "'timestamp' is required and must be an ISO 8601 date/time.");
        }

        var (observerHeightValid, observerHeight) = ElevationAnalysisValidation.ResolveHeight(request.ObserverHeight);
        if (!observerHeightValid)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'observerHeight' must be a finite, non-negative value in meters.");
        }

        if (request.MaxShadowLengthMeters is not { } maxShadowLength
            || !double.IsFinite(maxShadowLength) || maxShadowLength <= 0)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'maxShadowLengthMeters' is required and must be a positive finite distance in meters.");
        }

        if (request.SampleCount is { } sampleCount)
        {
            if (sampleCount < 2)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context,
                    "'sampleCount' must be at least 2 when supplied.");
            }

            // Enforce the same configured ceiling as the elevation profile endpoint:
            // the service forwards the requested count to the sampler, so an
            // unbounded value would drive unbounded DB sampling and allocation.
            var maxSampleCount = limitsOptions.Value.Elevation.MaxSampleCount;
            if (sampleCount > maxSampleCount)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context,
                    $"'sampleCount' ({sampleCount}) exceeds the configured maximum of {maxSampleCount}.");
            }
        }

        var validation = await ElevationAnalysisValidation.ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.Publication?.LayerIndex ?? 0;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.sun_shadow");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.sun-shadow");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await sceneService.ComputeSunShadowAsync(
                layerId,
                new ShadowObserver { Longitude = observerLon, Latitude = observerLat, HeightMeters = observerHeight },
                new SunShadowOptions
                {
                    InstantUtc = timestamp,
                    MaxShadowLengthMeters = maxShadowLength,
                    SampleCount = request.SampleCount
                },
                mergeStrategy,
                cancellationToken);

            activity?.SetTag("honua.elevation.sun_altitude", result.SolarPosition.AltitudeDegrees);
            activity?.SetTag("honua.elevation.shadow_length_m", result.ShadowLengthMeters);

            var response = BuildSunShadowResponse(datasetId, result, mergeStrategy);
            return Results.Json(response, SceneAnalysisJsonContext.Default.SunShadowResponse, contentType: JsonContentType);
        }
        catch (ElevationQueryException ex)
        {
            HonuaTelemetry.RecordException(activity, ex);
            return MapElevationException(context, ex);
        }
    }

    private static async Task<IResult> HandleSlice(
        string datasetId,
        HttpContext context,
        SceneAnalysisService sceneService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Honua.Elevation.SceneAnalysis");

        var gate = LicenseGate.RequireEntitlement(context, SliceEntitlement, "Slice Analysis", logger);
        if (gate is not null)
        {
            return gate;
        }

        SliceRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                SceneAnalysisJsonContext.Default.SliceRequest,
                cancellationToken);
        }
        // Intentional broad catch: request-body deserialization can fail for many reasons
        // (malformed JSON, unsupported content-type, client disconnect mid-read); all map
        // uniformly to a 400 bad-request response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Warning))
            {
                var sanitizedDatasetId = LogValueRedactor.SanitizeForLog(datasetId);
                LogSliceRequestParseFailed(logger, sanitizedDatasetId, ex);
            }
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON.");
        }

        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body is required.");
        }

        var (startLonValid, startLon, startLat) = ElevationAnalysisValidation.ResolveCoordinate(request.StartLon, request.StartLat);
        if (!startLonValid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Slice 'startLon' and 'startLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        var (endLonValid, endLon, endLat) = ElevationAnalysisValidation.ResolveCoordinate(request.EndLon, request.EndLat);
        if (!endLonValid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Slice 'endLon' and 'endLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        if (request.SampleCount is { } sampleCount)
        {
            if (sampleCount < 2)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context,
                    "'sampleCount' must be at least 2 when supplied.");
            }

            // Enforce the same configured ceiling as the elevation profile endpoint:
            // the service forwards the requested count to the sampler, so an
            // unbounded value would drive unbounded DB sampling and allocation.
            var maxSampleCount = limitsOptions.Value.Elevation.MaxSampleCount;
            if (sampleCount > maxSampleCount)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context,
                    $"'sampleCount' ({sampleCount}) exceeds the configured maximum of {maxSampleCount}.");
            }
        }

        if (Math.Abs(startLon - endLon) < CoordinateEqualityEpsilon &&
            Math.Abs(startLat - endLat) < CoordinateEqualityEpsilon)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Slice plane start and end must be distinct coordinates.");
        }

        var validation = await ElevationAnalysisValidation.ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.Publication?.LayerIndex ?? 0;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.slice");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.slice");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await sceneService.ComputeSliceAsync(
                layerId,
                new SlicePlane
                {
                    StartLongitude = startLon,
                    StartLatitude = startLat,
                    EndLongitude = endLon,
                    EndLatitude = endLat
                },
                request.SampleCount,
                mergeStrategy,
                cancellationToken);

            activity?.SetTag("honua.elevation.slice_length_m", result.LengthMeters);
            activity?.SetTag("honua.elevation.sample_count", result.SampleCount);

            var response = BuildSliceResponse(datasetId, result, mergeStrategy);
            return Results.Json(response, SceneAnalysisJsonContext.Default.SliceResponse, contentType: JsonContentType);
        }
        catch (ElevationQueryException ex)
        {
            HonuaTelemetry.RecordException(activity, ex);
            return MapElevationException(context, ex);
        }
    }

    private static IResult MapElevationException(HttpContext context, ElevationQueryException exception)
        => exception.FailureKind switch
        {
            ElevationFailureKind.SourceUnavailable => StandardErrorHelpers.CreateNotFound(context, exception.Message),
            ElevationFailureKind.UnsupportedCrs => StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            ElevationFailureKind.InvalidGeometry => StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            ElevationFailureKind.MisalignedMosaic => StandardErrorHelpers.CreateUnprocessableEntity(context, exception.Message),
            _ => StandardErrorHelpers.CreateBadRequest(context, exception.Message)
        };

    private static SunShadowResponse BuildSunShadowResponse(
        string datasetId,
        SunShadowResult result,
        RasterMergeStrategy mergeStrategy)
    {
        var samples = new ShadowSampleDto[result.Samples.Length];
        for (var i = 0; i < result.Samples.Length; i++)
        {
            var sample = result.Samples[i];
            samples[i] = new ShadowSampleDto
            {
                Lon = sample.Longitude,
                Lat = sample.Latitude,
                DistanceMeters = sample.DistanceMeters,
                TerrainElevation = sample.TerrainElevation,
                RayElevation = sample.RayElevation
            };
        }

        return new SunShadowResponse
        {
            DatasetId = datasetId,
            LayerId = result.LayerId,
            SolarPosition = new SolarPositionDto
            {
                AltitudeDegrees = result.SolarPosition.AltitudeDegrees,
                AzimuthDegrees = result.SolarPosition.AzimuthDegrees,
                DeclinationDegrees = result.SolarPosition.DeclinationDegrees,
                EquationOfTimeMinutes = result.SolarPosition.EquationOfTimeMinutes,
                HourAngleDegrees = result.SolarPosition.HourAngleDegrees,
                AboveHorizon = result.SolarPosition.IsAboveHorizon
            },
            ShadowCast = result.ShadowCast,
            NoShadowReason = result.NoShadowReason,
            ObserverGroundElevation = result.ObserverGroundElevation,
            ObserverTopElevation = result.ObserverTopElevation,
            ShadowAzimuthDegrees = result.ShadowAzimuthDegrees,
            ShadowLengthMeters = result.ShadowLengthMeters,
            TipLon = result.TipLongitude,
            TipLat = result.TipLatitude,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            Samples = samples
        };
    }

    private static SliceResponse BuildSliceResponse(
        string datasetId,
        SliceResult result,
        RasterMergeStrategy mergeStrategy)
    {
        var samples = new SliceSampleDto[result.Samples.Length];
        for (var i = 0; i < result.Samples.Length; i++)
        {
            var sample = result.Samples[i];
            samples[i] = new SliceSampleDto
            {
                Lon = sample.Longitude,
                Lat = sample.Latitude,
                DistanceMeters = sample.DistanceMeters,
                Elevation = sample.Elevation
            };
        }

        return new SliceResponse
        {
            DatasetId = datasetId,
            LayerId = result.LayerId,
            LengthMeters = result.LengthMeters,
            SampleCount = result.SampleCount,
            MinElevation = result.MinElevation,
            MaxElevation = result.MaxElevation,
            ReliefMeters = result.ReliefMeters,
            HasNoDataSamples = result.HasNoDataSamples,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            Samples = samples
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

    [LoggerMessage(EventId = 9450, Level = LogLevel.Warning, Message = "Failed to parse sun/shadow analysis request body for dataset {DatasetId}.")]
    private static partial void LogSunShadowRequestParseFailed(ILogger logger, string datasetId, Exception exception);

    [LoggerMessage(EventId = 9451, Level = LogLevel.Warning, Message = "Failed to parse slice analysis request body for dataset {DatasetId}.")]
    private static partial void LogSliceRequestParseFailed(ILogger logger, string datasetId, Exception exception);
}
