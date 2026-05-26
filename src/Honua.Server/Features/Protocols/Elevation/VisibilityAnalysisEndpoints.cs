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
/// Pro-tier 3D visibility analysis endpoints (line-of-sight and viewshed) that
/// build on the elevation profile sampler. Registered alongside the elevation
/// API and gated through <see cref="LicenseGate"/>.
/// </summary>
internal static class VisibilityAnalysisEndpoints
{
    private const string JsonContentType = "application/json";
    private const string LineOfSightEntitlement = "analytics.line-of-sight";
    private const string ViewshedEntitlement = "analytics.viewshed";

    public static IEndpointRouteBuilder MapVisibilityAnalysisEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // HANDLER-AUTHORIZED (#1144): these POST routes mirror the read-only
        // elevation API (GET /elevation/{datasetId}/profile). Access is enforced
        // inside the handler — LicenseGate.RequireEntitlement gates the Pro-tier
        // entitlement, and ValidateDatasetAsync runs the layer access check
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

        // HANDLER-AUTHORIZED (#1144): see the line-of-sight route above —
        // LicenseGate.RequireEntitlement + ValidateDatasetAsync (AccessScope.Read)
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
        IVisibilityAnalysisService visibilityService,
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

        if (!TryResolveCoordinate(request.TargetLon, request.TargetLat, out var targetLon, out var targetLat))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Target 'targetLon' and 'targetLat' are required and must be finite WGS 84 coordinates within longitude [-180, 180] and latitude [-90, 90].");
        }

        if (!TryResolveHeight(request.ObserverHeight, out var observerHeight)
            || !TryResolveHeight(request.TargetHeight, out var targetHeight))
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Height offsets must be finite, non-negative values in meters.");
        }

        if (request.SampleCount is { } sampleCount && sampleCount < 2)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'sampleCount' must be at least 2 when supplied.");
        }

        if (observerLon == targetLon && observerLat == targetLat)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Observer and target must be distinct coordinates.");
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.line_of_sight");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.line-of-sight");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var result = await visibilityService.ComputeLineOfSightAsync(
                layer.Id,
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
        IVisibilityAnalysisService visibilityService,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger("Honua.Elevation.Visibility");

        var gate = LicenseGate.RequireEntitlement(context, ViewshedEntitlement, "Viewshed", logger);
        if (gate is not null)
        {
            return gate;
        }

        ViewshedRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                VisibilityJsonContext.Default.ViewshedRequest,
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

        if (request.RadiusMeters is not { } radius || !double.IsFinite(radius) || radius <= 0)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'radiusMeters' is required and must be a positive finite distance in meters.");
        }

        if (!TryResolveHeight(request.ObserverHeight, out var observerHeight)
            || !TryResolveHeight(request.TargetHeight, out var targetHeight))
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "Height offsets must be finite, non-negative values in meters.");
        }

        if (request.RayCount is { } rayCount && rayCount < 1)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'rayCount' must be at least 1 when supplied.");
        }

        if (request.SamplesPerRay is { } samplesPerRay && samplesPerRay < 2)
        {
            return StandardErrorHelpers.CreateUnprocessableEntity(
                context,
                "'samplesPerRay' must be at least 2 when supplied.");
        }

        var validation = await ValidateDatasetAsync(context, datasetId, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ErrorResult!;
        }

        var layer = validation.Layer!;
        var mergeStrategy = RasterMosaicUtilities.ResolveMergeStrategy(layer.Metadata, request.MosaicRule);

        using var activity = HonuaTelemetry.StartActivity("honua.elevation.viewshed");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Elevation);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "elevation.viewshed");
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layer.Id);
        activity?.SetTag(HonuaTelemetry.Tags.CollectionId, datasetId);

        try
        {
            var options = new ViewshedOptions
            {
                RadiusMeters = radius,
                RayCount = request.RayCount,
                SamplesPerRay = request.SamplesPerRay,
                ObserverHeightOffsetMeters = observerHeight,
                TargetHeightOffsetMeters = targetHeight
            };

            var result = await visibilityService.ComputeViewshedAsync(
                layer.Id,
                new VisibilityPoint { Longitude = observerLon, Latitude = observerLat, HeightOffsetMeters = observerHeight },
                options,
                mergeStrategy,
                cancellationToken);

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
}
