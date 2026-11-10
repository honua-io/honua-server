// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
//
// MapServer reuse shims. These forwarders let the MapServer protocol surface
// invoke the existing FeatureServer generateRenderer and queryRelatedRecords
// handlers without duplicating their request parsing or validation. The
// MapServer routes pass the same {serviceId} and {layerId:int} route values
// those handlers read.

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    internal static Task<IResult> HandleGenerateRendererForMapServer(HttpContext context)
        => HandleGenerateRenderer(context);

    internal static Task<IResult> HandleGenerateRendererForMapServer(
        HttpContext context,
        IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        int layerId)
        => HandleGenerateRenderer(context, values, layerId);

    internal static Task<IResult> HandleQueryRelatedRecordsGetForMapServer(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsGetCore(serviceId, layerId, context, relatedRecordsHandler, "MapServer");

    internal static Task<IResult> HandleQueryRelatedRecordsPostForMapServer(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerRelatedRecordsHandler relatedRecordsHandler)
        => HandleQueryRelatedRecordsPostCore(serviceId, layerId, context, relatedRecordsHandler, "MapServer");
}
