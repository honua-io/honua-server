// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// MapServer reuse shims. These are thin, additive forwarders that let the
// MapServer protocol surface invoke the existing FeatureServer generateRenderer
// and queryRelatedRecords handlers *as-is* without duplicating their request
// parsing, validation, or renderer/related-records logic.
//
// IMPORTANT FOR SIBLING BRANCHES: this file only forwards to the existing
// private handlers in FeatureServerRequestHandlers.Renderer.cs and
// FeatureServerRequestHandlers.RelatedRecords.cs. It does not change their
// behavior. It exists as a *new* file so the MapServer adapter branch does not
// have to edit the FeatureServer handler files themselves (avoiding merge
// conflicts with a concurrent FeatureServer branch). The MapServer routes pass
// the same {serviceId} and {layerId:int} route values the FeatureServer
// handlers read, so the FeatureServer handlers resolve the service/layer
// identically. Protocol validation runs against the shared service record,
// which enables both FeatureServer and MapServer.

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// MapServer adapter entry point that reuses the FeatureServer
    /// generateRenderer handler verbatim. The MapServer route supplies the same
    /// route values (serviceId/layerId) the FeatureServer handler reads.
    /// </summary>
    internal static Task<IResult> HandleGenerateRendererForMapServer(HttpContext context)
        => HandleGenerateRenderer(context);

    /// <summary>
    /// MapServer adapter entry point that reuses the FeatureServer
    /// queryRelatedRecords (GET) handler verbatim.
    /// </summary>
    internal static Task<IResult> HandleQueryRelatedRecordsGetForMapServer(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsGet(serviceId, layerId, context, relatedRecordsHandler);

    /// <summary>
    /// MapServer adapter entry point that reuses the FeatureServer
    /// queryRelatedRecords (POST) handler verbatim.
    /// </summary>
    internal static Task<IResult> HandleQueryRelatedRecordsPostForMapServer(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsPost(serviceId, layerId, context, relatedRecordsHandler);
}
