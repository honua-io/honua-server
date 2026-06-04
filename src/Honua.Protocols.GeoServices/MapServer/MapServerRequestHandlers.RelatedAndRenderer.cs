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

using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
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
}
