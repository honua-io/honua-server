// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Raster;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Elevation;

/// <summary>
/// Pro-tier scene analysis endpoints (sun/shadow and slice/volumetric
/// cross-section) that build on the elevation profile sampler. Registered
/// alongside the elevation API and gated through <see cref="LicenseGate"/>.
/// </summary>
/// <remarks>
/// Like the other protocol mutation surfaces, these POST routes use
/// <c>AllowAnonymous()</c> to bypass the application-wide authorization policy
/// so the per-layer access check can run. Every handler invokes
/// <c>LayerValidationHelpers.ValidateLayerWithAccessAsync(... AccessScope.Read)</c>
/// (via <see cref="ValidateDatasetAsync"/>) plus the <see cref="LicenseGate"/>
/// entitlement check before touching the elevation surface, which is what
/// returns 401/403/402 for unauthenticated, unauthorized, or unlicensed
/// callers. Removing <c>AllowAnonymous()</c> without first moving the per-layer
/// gate into the pipeline would lock out every caller whose permissions live on
/// the layer rather than on a global role.
/// </remarks>
internal static class SceneAnalysisEndpoints
{
    private const string JsonContentType = "application/json";
    private const string SunShadowEntitlement = "analytics.sun-shadow";
    private const string SliceEntitlement = "analytics.slice";

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
        ISceneAnalysisService sceneService,
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
        catch
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON.");
        }

        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body is required.");
        }

        if (!TryResolveCoordinate(request.ObserverLon, request.ObserverLat, out var observerLon, out var observerLat))
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

        if (!TryResolveHeight(request.ObserverHeight, out var observerHeight))
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

        if (request.SampleCount is { } sampleCount && sampleCount < 2)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'sampleCount' must be at least 2 when supplied.");
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.sun_shadow");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.sun-shadow");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await sceneService.ComputeSunShadowAsync(
                layer.Id,
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
        ISceneAnalysisService sceneService,
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
        catch
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON.");
        }

        if (request is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body is required.");
        }

        if (!TryResolveCoordinate(request.StartLon, request.StartLat, out var startLon, out var startLat))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Slice 'startLon' and 'startLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        if (!TryResolveCoordinate(request.EndLon, request.EndLat, out var endLon, out var endLat))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Slice 'endLon' and 'endLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        if (request.SampleCount is { } sampleCount && sampleCount < 2)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'sampleCount' must be at least 2 when supplied.");
        }

        if (startLon == endLon && startLat == endLat)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Slice plane start and end must be distinct coordinates.");
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.slice");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.slice");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await sceneService.ComputeSliceAsync(
                layer.Id,
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

    private static bool TryResolveCoordinate(double? lon, double? lat, out double resolvedLon, out double resolvedLat)
    {
        resolvedLon = 0;
        resolvedLat = 0;
        if (lon is not { } x || lat is not { } y)
        {
            return false;
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        if (x < -180 || x > 180 || y < -90 || y > 90)
        {
            return false;
        }

        resolvedLon = x;
        resolvedLat = y;
        return true;
    }

    private static bool TryResolveHeight(double? height, out double resolvedHeight)
    {
        resolvedHeight = 0;
        if (height is not { } value)
        {
            return true;
        }

        if (!double.IsFinite(value) || value < 0)
        {
            return false;
        }

        resolvedHeight = value;
        return true;
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
}
