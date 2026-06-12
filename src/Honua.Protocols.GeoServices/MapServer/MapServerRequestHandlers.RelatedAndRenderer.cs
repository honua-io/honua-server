// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Thin MapServer adapters for the FeatureServer-style generateRenderer,
// queryRelatedRecords, and queryAttachments operations. Each adapter forwards to
// the existing FeatureServer handler/pipeline as-is (see the *.MapServerAdapters
// shims in the FeatureServer folder). MapServer adds no parsing or rendering
// logic of its own here: the FeatureServer handlers read the same
// {serviceId}/{layerId} route values and resolve the shared service record,
// which enables both the FeatureServer and MapServer protocols.

using System.Globalization;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// MapServer service-level <c>.../MapServer/generateRenderer</c> (GET/POST).
    /// Resolves the target layer from <c>layer</c> or <c>layerId</c>, then reuses
    /// the FeatureServer generateRenderer pipeline.
    /// </summary>
    private static async Task<IResult> HandleMapServerServiceGenerateRenderer(string serviceId, HttpContext context)
    {
        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);
        var values = GeoServicesRequestValueHelpers.ToCaseInsensitiveDictionary(context.Request.Query);
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            var (bodyValues, readError) = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(
                context.Request,
                cancellationToken).ConfigureAwait(false);
            if (bodyValues is null)
            {
                if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(
                        context,
                        receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    [readError ?? "Invalid request body."]);
            }

            foreach (var pair in bodyValues)
            {
                values[pair.Key] = pair.Value;
            }
        }

        if (!TryResolveServiceGenerateRendererLayer(values, out var layerId, out var layerError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [layerError ?? "layer is required."]);
        }

        values.Remove("layer");
        values.Remove("layerId");

        return await FeatureServerEndpoints.HandleGenerateRendererForMapServer(
            context,
            values,
            layerId).ConfigureAwait(false);
    }

    /// <summary>
    /// MapServer <c>.../MapServer/{layerId}/generateRenderer</c> (GET/POST).
    /// Reuses the FeatureServer generateRenderer handler verbatim.
    /// </summary>
    private static Task<IResult> HandleMapServerGenerateRenderer(string serviceId, int layerId, HttpContext context)
        => FeatureServerEndpoints.HandleGenerateRendererForMapServer(context);

    /// <summary>
    /// MapServer <c>.../MapServer/{layerId}/queryRelatedRecords</c> (GET).
    /// Reuses the FeatureServer queryRelatedRecords GET handler verbatim.
    /// </summary>
    private static Task<IResult> HandleMapServerQueryRelatedRecordsGet(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => FeatureServerEndpoints.HandleQueryRelatedRecordsGetForMapServer(serviceId, layerId, context, relatedRecordsHandler);

    /// <summary>
    /// MapServer <c>.../MapServer/{layerId}/queryRelatedRecords</c> (POST).
    /// Reuses the FeatureServer queryRelatedRecords POST handler verbatim.
    /// </summary>
    private static Task<IResult> HandleMapServerQueryRelatedRecordsPost(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => FeatureServerEndpoints.HandleQueryRelatedRecordsPostForMapServer(serviceId, layerId, context, relatedRecordsHandler);

    /// <summary>
    /// MapServer <c>.../MapServer/{layerId}/queryAttachments</c> (GET/POST).
    /// Reuses the FeatureServer queryAttachments handler verbatim.
    /// </summary>
    private static Task HandleMapServerQueryAttachments(HttpContext context)
        => AttachmentEndpoints.HandleQueryAttachmentsForMapServer(context);

    private static bool TryResolveServiceGenerateRendererLayer(
        IReadOnlyDictionary<string, StringValues> values,
        out int layerId,
        out string? error)
    {
        layerId = default;
        error = null;

        var raw = GeoServicesRequestValueHelpers.GetValueString(values, "layer")
            ?? GeoServicesRequestValueHelpers.GetValueString(values, "layerId");
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "layer or layerId is required.";
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out layerId) ||
            layerId < 0)
        {
            error = "layer must be a non-negative integer.";
            return false;
        }

        return true;
    }
}
