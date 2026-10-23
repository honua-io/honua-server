// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// MapServer reuse shims. These forwarders let the MapServer protocol surface
// invoke the existing FeatureServer generateRenderer and queryRelatedRecords
// handlers without duplicating their request parsing or validation. The
// MapServer routes pass the same {serviceId} and {layerId:int} route values
// those handlers read.

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
    /// MapServer service-level adapter entry point for <c>generateRenderer</c>.
    /// The MapServer wrapper resolves the target layer from request parameters,
    /// then forwards the remaining renderer parameters into the FeatureServer
    /// implementation.
    /// </summary>
    internal static Task<IResult> HandleGenerateRendererForMapServer(
        HttpContext context,
        IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        int layerId)
        => HandleGenerateRenderer(context, values, layerId);

    /// <summary>
    /// MapServer adapter entry point that reuses the FeatureServer
    /// queryRelatedRecords (GET) handler verbatim.
    /// </summary>
    internal static Task<IResult> HandleQueryRelatedRecordsGetForMapServer(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsGetCore(serviceId, layerId, context, relatedRecordsHandler, "MapServer");

    /// <summary>
    /// MapServer adapter entry point that reuses the FeatureServer
    /// queryRelatedRecords (POST) handler verbatim.
    /// </summary>
    internal static Task<IResult> HandleQueryRelatedRecordsPostForMapServer(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsPostCore(serviceId, layerId, context, relatedRecordsHandler, "MapServer");
}
