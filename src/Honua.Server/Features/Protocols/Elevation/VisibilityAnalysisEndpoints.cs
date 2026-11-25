// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Helpers;
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

internal static partial class VisibilityAnalysisEndpoints
{
    private const string JsonContentType = "application/json";
    private const string LineOfSightEntitlement = "analytics.line-of-sight";
    private const string ViewshedEntitlement = "analytics.viewshed";

    private const double CoordinateEqualityEpsilon = 1e-9;
    private const int MaxViewshedRayCount = VisibilityAnalysisService.DefaultRayCount;
    private const int MaxViewshedSampleBudgetMultiplier = 16;
    private const double MaxViewshedRadiusMeters = 100_000.0;

    public static IEndpointRouteBuilder MapVisibilityAnalysisEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // HANDLER-AUTHORIZED: these POST routes mirror the read-only
        // elevation API (GET /elevation/{datasetId}/profile). Access is enforced
        // inside the handler — LicenseGate.RequireEntitlement gates the Pro-tier
        // entitlement, and ElevationAnalysisValidation.ValidateDatasetAsync runs the layer access check
        // (AccessScope.Read) before any analysis. Marked AllowAnonymous so the
        // audit architecture guard records the explicit (read-only) decision.
        endpoints.MapPost("/elevation/{datasetId}/line-of-sight", HandleLineOfSight)
            .WithDisplayName("Compute Line of Sight")
            .WithName("ComputeLineOfSight")
            .WithSummary("Determine terrain visibility between an observer and a target")
            .WithDescription("Samples the elevation profile between an observer and target and reports whether the target is visible, returning the first terrain obstruction when blocked")
            .WithTags("Elevation")
            .Accepts<LineOfSightRequest>(JsonContentType)
            .Produces<LineOfSightResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status402PaymentRequired)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .AllowAnonymous();

        // HANDLER-AUTHORIZED: see the line-of-sight route above —
        // LicenseGate.RequireEntitlement + ElevationAnalysisValidation.ValidateDatasetAsync (AccessScope.Read)
        // enforce access inside the handler. Marked AllowAnonymous so the audit
        // architecture guard records the explicit (read-only) decision.
        endpoints.MapPost("/elevation/{datasetId}/viewshed", HandleViewshed)
            .WithDisplayName("Compute Viewshed")
            .WithName("ComputeViewshed")
            .WithSummary("Compute the visible area around an observer over the elevation surface")
            .WithDescription("Casts azimuth rays around an observer and reports which radially-sampled points are visible over the elevation surface within a radius")
            .WithTags("Elevation")
            .Accepts<ViewshedRequest>(JsonContentType)
            .Produces<ViewshedResponse>(StatusCodes.Status200OK, JsonContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status402PaymentRequired)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> HandleLineOfSight(
        string datasetId,
        HttpContext context,
        VisibilityAnalysisService visibilityService,
        [FromServices] IOptions<LimitsOptions> limitsOptions,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Honua.Elevation.Visibility");

        var gate = LicenseGate.RequireEntitlement(context, LineOfSightEntitlement, "Line of sight", logger);
        if (gate is not null)
        {
            return gate;
        }

        LineOfSightRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                VisibilityJsonContext.Default.LineOfSightRequest,
                cancellationToken);
        }
        // Intentional broad catch: this is the request-body parse boundary for the line-of-sight
        // endpoint; any malformed-JSON failure is logged and mapped to a 400 response below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Warning))
            {
                var sanitizedDatasetId = LogValueRedactor.SanitizeForLog(datasetId);
                LogLineOfSightRequestParseFailed(logger, sanitizedDatasetId, ex);
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

        var (targetLonValid, targetLon, targetLat) = ElevationAnalysisValidation.ResolveCoordinate(request.TargetLon, request.TargetLat);
        if (!targetLonValid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Target 'targetLon' and 'targetLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        var (observerHeightValid, observerHeight) = ElevationAnalysisValidation.ResolveHeight(request.ObserverHeight);
        var (targetHeightValid, targetHeight) = ElevationAnalysisValidation.ResolveHeight(request.TargetHeight);
        if (!observerHeightValid || !targetHeightValid)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Height offsets must be finite, non-negative values in meters.");
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

        if (Math.Abs(observerLon - targetLon) < CoordinateEqualityEpsilon &&
            Math.Abs(observerLat - targetLat) < CoordinateEqualityEpsilon)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Observer and target must be distinct coordinates.");
        }

        var validation = await ElevationAnalysisValidation.ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.LayerId;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.line_of_sight");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.line-of-sight");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await visibilityService.ComputeLineOfSightAsync(
                layerId,
                new VisibilityPoint { Longitude = observerLon, Latitude = observerLat, HeightOffsetMeters = observerHeight },
                new VisibilityPoint { Longitude = targetLon, Latitude = targetLat, HeightOffsetMeters = targetHeight },
                request.SampleCount,
                mergeStrategy,
                cancellationToken);

            activity?.SetTag("honua.elevation.visible", result.Visible);
            activity?.SetTag("honua.elevation.distance_m", result.DistanceMeters);

            var response = BuildLineOfSightResponse(datasetId, result, mergeStrategy);
            return Results.Json(response, VisibilityJsonContext.Default.LineOfSightResponse, contentType: JsonContentType);
        }
        catch (ElevationQueryException ex)
        {
            HonuaTelemetry.RecordException(activity, ex);
            return MapElevationException(context, ex);
        }
    }

    private static async Task<IResult> HandleViewshed(
        string datasetId,
        HttpContext context,
        VisibilityAnalysisService visibilityService)
    {
        var effectiveToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>();
        var logger = context.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Honua.Elevation.Visibility");

        var gate = LicenseGate.RequireEntitlement(context, ViewshedEntitlement, "Viewshed", logger);
        if (gate is not null)
        {
            return gate;
        }

        var (request, parseError) = await ReadViewshedRequestAsync(context, datasetId, logger).ConfigureAwait(false);
        if (parseError is not null)
        {
            return parseError;
        }

        var (validated, validationError) = ValidateViewshedParameters(context, request!, limitsOptions.Value.Elevation.MaxSampleCount);
        if (validationError is not null)
        {
            return validationError;
        }

        var validation = await ElevationAnalysisValidation.ValidateDatasetAsync(context, datasetId, effectiveToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var resource = validation.Resource!;
        var layerId = validation.LayerId;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(resource, request!.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.viewshed");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.viewshed");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await visibilityService.ComputeViewshedAsync(
                layerId,
                validated!.Observer,
                validated.Options,
                mergeStrategy,
                effectiveToken);

            activity?.SetTag("honua.elevation.ray_count", result.RayCount);
            activity?.SetTag("honua.elevation.visible_samples", result.VisibleSampleCount);

            var response = BuildViewshedResponse(datasetId, result, mergeStrategy);
            return Results.Json(response, VisibilityJsonContext.Default.ViewshedResponse, contentType: JsonContentType);
        }
        catch (ElevationQueryException ex)
        {
            HonuaTelemetry.RecordException(activity, ex);
            return MapElevationException(context, ex);
        }
    }

    private sealed class ValidatedViewshedInput
    {
        public required VisibilityPoint Observer { get; init; }
        public required ViewshedOptions Options { get; init; }
    }

    private static async Task<(ViewshedRequest? Request, IResult? Error)> ReadViewshedRequestAsync(
        HttpContext context,
        string datasetId,
        ILogger? logger)
    {
        var effectiveToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        try
        {
            var request = await context.Request.ReadFromJsonAsync(
                VisibilityJsonContext.Default.ViewshedRequest,
                effectiveToken).ConfigureAwait(false);
            if (request is null)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Request body is required."));
            }

            return (request, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (logger is not null && logger.IsEnabled(LogLevel.Warning))
            {
                var sanitizedDatasetId = LogValueRedactor.SanitizeForLog(datasetId);
                LogViewshedRequestParseFailed(logger, sanitizedDatasetId, ex);
            }

            return (null, StandardErrorHelpers.CreateBadRequest(context, "Request body must be valid JSON."));
        }
    }

    private static (ValidatedViewshedInput? Input, IResult? Error) ValidateViewshedParameters(
        HttpContext context,
        ViewshedRequest request,
        int maxSampleCount)
    {
        var (observerLonValid, observerLon, observerLat) = ElevationAnalysisValidation.ResolveCoordinate(request.ObserverLon, request.ObserverLat);
        if (!observerLonValid)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context,
                "Observer 'observerLon' and 'observerLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90]."));
        }

        var (radius, radiusError) = ValidateViewshedRadius(context, request.RadiusMeters);
        if (radiusError is not null)
        {
            return (null, radiusError);
        }

        var (observerHeightValid, observerHeight) = ElevationAnalysisValidation.ResolveHeight(request.ObserverHeight);
        var (targetHeightValid, targetHeight) = ElevationAnalysisValidation.ResolveHeight(request.TargetHeight);
        if (!observerHeightValid || !targetHeightValid)
        {
            return (null, StandardErrorHelpers.CreateUnprocessableEntity(context, "Height offsets must be finite, non-negative values in meters."));
        }

        var (effectiveRayCount, effectiveSamplesPerRay, budgetError) = ValidateViewshedSamplingBudget(context, request, maxSampleCount);
        if (budgetError is not null)
        {
            return (null, budgetError);
        }

        return (new ValidatedViewshedInput
        {
            Observer = new VisibilityPoint { Longitude = observerLon, Latitude = observerLat, HeightOffsetMeters = observerHeight },
            Options = new ViewshedOptions
            {
                RadiusMeters = radius,
                RayCount = request.RayCount ?? effectiveRayCount,
                SamplesPerRay = request.SamplesPerRay ?? effectiveSamplesPerRay,
                ObserverHeightOffsetMeters = observerHeight,
                TargetHeightOffsetMeters = targetHeight
            }
        }, null);
    }

    private static (double Radius, IResult? Error) ValidateViewshedRadius(HttpContext context, double? radiusMeters)
    {
        if (radiusMeters is not { } radius || !double.IsFinite(radius) || radius <= 0)
        {
            return (0, StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'radiusMeters' is required and must be a positive finite distance in meters."));
        }

        if (radius > MaxViewshedRadiusMeters)
        {
            return (0, StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"'radiusMeters' ({radius}) exceeds the configured maximum of {MaxViewshedRadiusMeters} meters."));
        }

        return (radius, null);
    }

    private static (int RayCount, int SamplesPerRay, IResult? Error) ValidateViewshedSamplingBudget(
        HttpContext context,
        ViewshedRequest request,
        int maxSampleCount)
    {
        if (ValidateRayAndSampleBounds(context, request, maxSampleCount) is { } boundsError)
        {
            return (0, 0, boundsError);
        }

        var effectiveRayCount = Math.Max(request.RayCount ?? VisibilityAnalysisService.DefaultRayCount, VisibilityAnalysisService.MinRayCount);
        var effectiveSamplesPerRay = request.SamplesPerRay ?? Math.Min(VisibilityAnalysisService.DefaultSamplesPerRay, maxSampleCount);
        var maxTotalSamples = (long)maxSampleCount * MaxViewshedSampleBudgetMultiplier;
        var totalSamples = (long)effectiveRayCount * effectiveSamplesPerRay;
        if (totalSamples > maxTotalSamples)
        {
            return (0, 0, StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                $"Total viewshed samples ({totalSamples}) exceeds the configured maximum of {maxTotalSamples}."));
        }

        return (effectiveRayCount, effectiveSamplesPerRay, null);
    }

    private static IResult? ValidateRayAndSampleBounds(HttpContext context, ViewshedRequest request, int maxSampleCount)
    {
        if (request.RayCount is { } rayCount)
        {
            if (rayCount < 1)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(context, "'rayCount' must be at least 1 when supplied.");
            }

            if (rayCount > MaxViewshedRayCount)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context, $"'rayCount' ({rayCount}) exceeds the configured maximum of {MaxViewshedRayCount}.");
            }
        }

        if (request.SamplesPerRay is { } samplesPerRay)
        {
            if (samplesPerRay < 2)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(context, "'samplesPerRay' must be at least 2 when supplied.");
            }

            if (samplesPerRay > maxSampleCount)
            {
                return StandardErrorHelpers.CreateUnprocessableEntity(
                    context, $"'samplesPerRay' ({samplesPerRay}) exceeds the configured maximum of {maxSampleCount}.");
            }
        }

        return null;
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

    private static LineOfSightResponse BuildLineOfSightResponse(
        string datasetId,
        LineOfSightResult result,
        RasterMergeStrategy mergeStrategy)
    {
        return new LineOfSightResponse
        {
            DatasetId = datasetId,
            LayerId = result.LayerId,
            Visible = result.Visible,
            DistanceMeters = result.DistanceMeters,
            ObserverGroundElevation = result.ObserverGroundElevation,
            ObserverElevation = result.ObserverElevation,
            TargetGroundElevation = result.TargetGroundElevation,
            TargetElevation = result.TargetElevation,
            SampleCount = result.SampleCount,
            HasNoDataSamples = result.HasNoDataSamples,
            MosaicRule = FormatMergeStrategy(mergeStrategy),
            Obstruction = result.Obstruction is { } obstruction
                ? new LineOfSightObstructionDto
                {
                    Lon = obstruction.Longitude,
                    Lat = obstruction.Latitude,
                    Elevation = obstruction.Elevation,
                    DistanceMeters = obstruction.DistanceMeters
                }
                : null
        };
    }

    private static ViewshedResponse BuildViewshedResponse(
        string datasetId,
        ViewshedResult result,
        RasterMergeStrategy mergeStrategy)
    {
        var samples = new ViewshedSampleDto[result.Samples.Length];
        for (var i = 0; i < result.Samples.Length; i++)
        {
            var sample = result.Samples[i];
            samples[i] = new ViewshedSampleDto
            {
                Lon = sample.Longitude,
                Lat = sample.Latitude,
                AzimuthDegrees = sample.AzimuthDegrees,
                DistanceMeters = sample.DistanceMeters,
                Elevation = sample.Elevation,
                Visible = sample.Visible
            };
        }

        return new ViewshedResponse
        {
            DatasetId = datasetId,
            LayerId = result.LayerId,
            ObserverGroundElevation = result.ObserverGroundElevation,
            ObserverElevation = result.ObserverElevation,
            ObserverNoData = result.ObserverNoData,
            RadiusMeters = result.RadiusMeters,
            RayCount = result.RayCount,
            SamplesPerRay = result.SamplesPerRay,
            SampleCount = result.Samples.Length,
            VisibleSampleCount = result.VisibleSampleCount,
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

    [LoggerMessage(EventId = 9452, Level = LogLevel.Warning, Message = "Failed to parse line-of-sight analysis request body for dataset {DatasetId}.")]
    private static partial void LogLineOfSightRequestParseFailed(ILogger logger, string datasetId, Exception exception);

    [LoggerMessage(EventId = 9453, Level = LogLevel.Warning, Message = "Failed to parse viewshed analysis request body for dataset {DatasetId}.")]
    private static partial void LogViewshedRequestParseFailed(ILogger logger, string datasetId, Exception exception);
}
