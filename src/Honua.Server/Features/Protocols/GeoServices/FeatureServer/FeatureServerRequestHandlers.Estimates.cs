// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleGetEstimates(
        string serviceId,
        int layerId,
        HttpContext context,
        [FromServices] ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.GetEstimates, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "getEstimates",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
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
        var publication = validationResult.Publication!;
        var resource = validationResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
        if (accessError != null)
        {
            return accessError;
        }

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = snapshot.ResolveStorageLayerId(publication);
        if (storageLayerId is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                $"Layer '{resource.Metadata.Name ?? layerId.ToString(CultureInfo.InvariantCulture)}' is not bound to a storage layer.");
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var estimates = await featureReader.GetEstimatesAsync(storageLayerId.Value, cancellationToken);

        var response = new GetEstimatesResponse
        {
            Count = estimates.EstimatedCount,
            Extent = estimates.Extent.HasValue ? estimates.Extent.Value.ToExtentInfo() : null
        };

        scope.SetSuccess((int)Math.Min(response.Count, int.MaxValue));
        return Results.Json(
            response,
            FeatureServerJsonContext.Default.GetEstimatesResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleServiceGetEstimates(
        string serviceId,
        HttpContext context,
        [FromServices] ICommonQueryValidator queryValidator)
    {
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.ServiceGetEstimates, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var requestedFormat = context.Request.Query.TryGetValue("f", out var formatValue)
            ? formatValue.ToString()
            : null;
        if (!TryValidateOutputFormat(requestedFormat, JsonOnlyFormats, out _, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid query parameters",
                [formatError ?? "Output format is not supported."]);
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "getEstimates",
            HonuaTelemetry.Protocols.FeatureServer,
            "service",
            context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var serviceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceV2Async(
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
        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        if (!TryResolveRequestedServiceLayersV2(service, snapshot, values, out var selectedLayers, out _, out var selectionError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid query parameters",
                [selectionError ?? "Invalid layer selection."]);
        }

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            selectedLayers.Select(pair => pair.Resource),
            service);
        if (accessError != null)
        {
            return accessError;
        }

        var accessibleLayers = FilterAccessibleLayersV2(context, service, selectedLayers);
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var layerEstimates = new List<ServiceLayerEstimateInfo>(accessibleLayers.Length);

        foreach (var (publication, resource) in accessibleLayers)
        {
            var storageLayerId = snapshot.ResolveStorageLayerId(publication);
            if (storageLayerId is null)
            {
                // Publication isn't bound to a storage layer; skip rather than fail the whole
                // request — mirrors the v1 path which silently skipped unbound layers.
                continue;
            }

            var resolvedLayerId = publication.LayerIndex ?? storageLayerId.Value;
            var estimates = await featureReader.GetEstimatesAsync(storageLayerId.Value, cancellationToken);
            layerEstimates.Add(new ServiceLayerEstimateInfo
            {
                Id = resolvedLayerId,
                Count = estimates.EstimatedCount,
                Extent = estimates.Extent.HasValue ? estimates.Extent.Value.ToExtentInfo() : null
            });
        }

        var response = new ServiceGetEstimatesResponse
        {
            Layers = [.. layerEstimates]
        };

        scope.SetSuccess(response.Layers.Length);
        return Results.Json(
            response,
            FeatureServerJsonContext.Default.ServiceGetEstimatesResponse,
            contentType: "application/json");
    }
}
