// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle MapServer layer query (GET) - redirects to FeatureServer query endpoint.
    /// MapServer query uses the same Esri REST API contract as FeatureServer.
    /// </summary>
    private static IResult HandleLayerQueryGet(string serviceId, int layerId, HttpContext context)
    {
        var featureServerUrl = $"/rest/services/{serviceId}/FeatureServer/{layerId}/query{context.Request.QueryString}";
        return Results.Redirect(featureServerUrl, preserveMethod: true);
    }

    /// <summary>
    /// Handle MapServer layer query (POST) - redirects to FeatureServer query endpoint.
    /// Uses HTTP 307 to preserve the POST method and body.
    /// </summary>
    private static IResult HandleLayerQueryPost(string serviceId, int layerId, HttpContext context)
    {
        var featureServerUrl = $"/rest/services/{serviceId}/FeatureServer/{layerId}/query{context.Request.QueryString}";
        return Results.Redirect(featureServerUrl, preserveMethod: true);
    }
}
