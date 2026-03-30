// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleGetEstimates(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.getEstimates");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.FeatureServer);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "getEstimates");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var layer = validationResult.Layer!;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var estimates = await featureReader.GetEstimatesAsync(layer.Id, cancellationToken);

        ExtentInfo? extentInfo = null;
        if (estimates.Extent.HasValue)
        {
            var ext = estimates.Extent.Value;
            extentInfo = new ExtentInfo
            {
                Xmin = ext.MinX,
                Ymin = ext.MinY,
                Xmax = ext.MaxX,
                Ymax = ext.MaxY,
                SpatialReference = layer.SpatialReference.ToSpatialReferenceInfo()
            };
        }

        var response = new GetEstimatesResponse
        {
            Count = estimates.EstimatedCount,
            Extent = extentInfo
        };

        return Results.Json(response, FeatureServerJsonContext.Default.GetEstimatesResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleServiceGetEstimates(
        string serviceId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.serviceGetEstimates");
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!serviceValidationResult.IsValid)
        {
            return serviceValidationResult.ErrorResult!;
        }

        var service = serviceValidationResult.Service!;
        var accessError = AccessPolicyHelpers.RequireServiceAccess(context, service);
        if (accessError != null)
        {
            return accessError;
        }

        // Aggregate estimates across all layers
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        long totalCount = 0;
        double? xmin = null, ymin = null, xmax = null, ymax = null;

        foreach (var layer in service.Layers)
        {
            var estimates = await featureReader.GetEstimatesAsync(layer.Id, cancellationToken);
            totalCount += estimates.EstimatedCount;

            if (estimates.Extent.HasValue)
            {
                var ext = estimates.Extent.Value;
                xmin = xmin.HasValue ? Math.Min(xmin.Value, ext.MinX) : ext.MinX;
                ymin = ymin.HasValue ? Math.Min(ymin.Value, ext.MinY) : ext.MinY;
                xmax = xmax.HasValue ? Math.Max(xmax.Value, ext.MaxX) : ext.MaxX;
                ymax = ymax.HasValue ? Math.Max(ymax.Value, ext.MaxY) : ext.MaxY;
            }
        }

        ExtentInfo? extentInfo = null;
        if (xmin.HasValue)
        {
            extentInfo = new ExtentInfo
            {
                Xmin = xmin.Value,
                Ymin = ymin!.Value,
                Xmax = xmax!.Value,
                Ymax = ymax!.Value,
                SpatialReference = service.SpatialReference.ToSpatialReferenceInfo()
            };
        }

        var response = new GetEstimatesResponse
        {
            Count = totalCount,
            Extent = extentInfo
        };

        return Results.Json(response, FeatureServerJsonContext.Default.GetEstimatesResponse, contentType: "application/json");
    }
}
