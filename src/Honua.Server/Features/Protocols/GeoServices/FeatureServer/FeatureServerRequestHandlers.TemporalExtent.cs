// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleTemporalExtent(
        string serviceId,
        int layerId,
        HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("featureserver.temporalExtent");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.FeatureServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.LayerMetadata, out var paramError))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Invalid query parameters",
                [paramError ?? "Invalid query parameter."]);
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

        // Discovery is opt-in (docs/gis/temporal-animation-api.md): a layer is
        // exposed via temporalExtent only when an explicit TimeInfo.StartTimeField
        // has been configured. The shared TryResolveTemporalRangeAsync helper
        // falls back to the first Date/DateTime attribute when TimeInfo is null
        // (preserved for OGC API Features collection extents), but that fallback
        // would surface temporal metadata for layers that were never marked
        // time-aware. WMS/WMTS already gate on the same condition before calling
        // the helper.
        if (layer.Metadata?.TimeInfo is null ||
            string.IsNullOrWhiteSpace(layer.Metadata.TimeInfo.StartTimeField))
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                $"Layer '{layer.Name ?? layer.Id.ToString(CultureInfo.InvariantCulture)}' is not configured as time-aware.");
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var temporalRange = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer,
            featureReader,
            cancellationToken).ConfigureAwait(false);

        if (temporalRange is null)
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                $"Layer '{layer.Name ?? layer.Id.ToString(CultureInfo.InvariantCulture)}' is not configured as time-aware.");
        }

        var range = temporalRange.Value;
        var response = new TemporalExtentResponse
        {
            LayerId = layer.Id,
            LayerName = layer.Name,
            StartTimeField = range.StartField.Name,
            EndTimeField = range.EndField?.Name,
            Min = range.Min?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Max = range.Max?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            MinEpochMs = range.Min?.ToUnixTimeMilliseconds(),
            MaxEpochMs = range.Max?.ToUnixTimeMilliseconds()
        };

        activity?.SetTag("featureserver.temporal.has_extent", range.HasExtent);
        return Results.Json(
            response,
            FeatureServerJsonContext.Default.TemporalExtentResponse,
            contentType: "application/json");
    }
}
