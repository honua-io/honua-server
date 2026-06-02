// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Route registration for the previously not-implemented FeatureServer operations.
/// These are thin protocol adapters over the shared service/layer validation and
/// access pipeline; they return Esri-spec-shaped responses (empty collections or
/// honest spec-compliant errors) for surfaces whose backing data model Honua does
/// not yet provide (contingent values, shared templates, HTML pop-ups, the image
/// resource, layer assets, 3D geometry, and metadata updates).
/// </summary>
internal static partial class FeatureServerEndpoints
{
    /// <summary>
    /// Maps the service-level and layer-level FeatureServer operations that complete
    /// Esri REST coverage with honest, specification-shaped responses.
    /// </summary>
    private static void MapFeatureServerNotImplementedEndpoints(IEndpointRouteBuilder endpoints)
    {
        // ----- Service-level operations -----

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/queryContingentValues", HandleQueryContingentValues)
            .WithDisplayName("Query Contingent Values")
            .WithName("QueryContingentValues")
            .WithSummary("Query contingent attribute value definitions for the service")
            .WithDescription("Returns the contingent values definition document. The collection is empty until contingent value modeling is implemented.")
            .WithTags("FeatureServer")
            .Produces<QueryContingentValuesResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/sharedTemplates", HandleSharedTemplates)
            .WithDisplayName("Get Shared Templates")
            .WithName("SharedTemplates")
            .WithSummary("List shared editing templates for the service")
            .WithDescription("Returns the shared editing template collection. The collection is empty until shared template storage is implemented.")
            .WithTags("FeatureServer")
            .Produces<SharedTemplatesResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/sharedTemplates/query", HandleSharedTemplatesQuery)
            .WithDisplayName("Query Shared Templates")
            .WithName("SharedTemplatesQuery")
            .WithSummary("Query shared editing templates for the service")
            .WithDescription("Returns matching shared editing templates. The collection is empty until shared template storage is implemented.")
            .WithTags("FeatureServer")
            .Produces<SharedTemplatesResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/sharedTemplates/add", HandleSharedTemplatesAdd)
            .WithDisplayName("Add Shared Templates")
            .WithName("SharedTemplatesAdd")
            .WithSummary("Add shared editing templates to the service")
            .WithDescription("Honestly rejects shared template additions; this server does not persist shared editing templates.")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/sharedTemplates/update", HandleSharedTemplatesUpdate)
            .WithDisplayName("Update Shared Templates")
            .WithName("SharedTemplatesUpdate")
            .WithSummary("Update shared editing templates on the service")
            .WithDescription("Honestly rejects shared template updates; this server does not persist shared editing templates.")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/sharedTemplates/delete", HandleSharedTemplatesDelete)
            .WithDisplayName("Delete Shared Templates")
            .WithName("SharedTemplatesDelete")
            .WithSummary("Delete shared editing templates from the service")
            .WithDescription("Honestly rejects shared template deletions; this server does not persist shared editing templates.")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/htmlPopup", HandleServiceHtmlPopup)
            .WithDisplayName("Get HTML Popup")
            .WithName("ServiceHtmlPopup")
            .WithSummary("Get the HTML pop-up definition for the service")
            .WithDescription("Returns the HTML pop-up type. Always esriServerHTMLPopupTypeNone until author-defined pop-up content is supported.")
            .WithTags("FeatureServer")
            .Produces<HtmlPopupResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/image", HandleServiceImage)
            .WithDisplayName("Get Service Image")
            .WithName("ServiceImage")
            .WithSummary("Get the image resource for the service")
            .WithDescription("Returns 404 honestly; this server does not store author-attached feature images served by the FeatureServer image resource.")
            .WithTags("FeatureServer")
            .Produces(404);

        // ----- Layer-level operations -----

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/hasAssets", HandleHasAssets)
            .WithDisplayName("Has Assets")
            .WithName("HasAssets")
            .WithSummary("Report whether the layer has stored assets")
            .WithDescription("Returns hasAssets=false; this server does not provide a layer asset store.")
            .WithTags("FeatureServer")
            .Produces<HasAssetsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryAssets", HandleQueryAssets)
            .WithDisplayName("Query Assets")
            .WithName("QueryAssets")
            .WithSummary("Query stored assets for the layer")
            .WithDescription("Returns an empty asset collection; this server does not provide a layer asset store.")
            .WithTags("FeatureServer")
            .Produces<QueryAssetsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/cleanupAssets", HandleCleanupAssets)
            .WithDisplayName("Cleanup Assets")
            .WithName("CleanupAssets")
            .WithSummary("Clean up orphaned assets for the layer")
            .WithDescription("No-op cleanup; this server does not provide a layer asset store, so no orphaned assets exist.")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces<CleanupAssetsResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/uploadAssets", HandleUploadAssets)
            .WithDisplayName("Upload Assets")
            .WithName("UploadAssets")
            .WithSummary("Upload assets to the layer")
            .WithDescription("Honestly rejects uploads; this server does not provide a layer asset store.")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/convert3D", HandleConvert3D)
            .WithDisplayName("Convert 3D")
            .WithName("Convert3D")
            .WithSummary("Convert the layer to a 3D representation")
            .WithDescription("Honestly rejects 3D conversion; this server does not provide a 3D geometry conversion pipeline.")
            .WithTags("FeatureServer")
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId:int}/query3D", HandleQuery3D)
            .WithDisplayName("Query 3D")
            .WithName("Query3D")
            .WithSummary("Query the layer's 3D features")
            .WithDescription("Honestly rejects 3D queries; this server does not expose a 3D query surface.")
            .WithTags("FeatureServer")
            .Produces(400)
            .Produces(404);

        endpoints.MapPost("/rest/services/{serviceId}/FeatureServer/{layerId:int}/metadata/update", HandleUpdateMetadata)
            .WithDisplayName("Update Metadata")
            .WithName("UpdateMetadata")
            .WithSummary("Update the layer's metadata document")
            .WithDescription("Honestly rejects metadata updates; layer metadata is authored through the Honua Admin API.")
            .WithTags("FeatureServer")
            .AllowAnonymous()
            .Produces(400)
            .Produces(404);
    }
}
