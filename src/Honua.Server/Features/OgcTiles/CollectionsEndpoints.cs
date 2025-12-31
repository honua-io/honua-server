// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OgcTiles;

internal static class CollectionsEndpoints
{
    public static IEndpointRouteBuilder MapCollectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/tiles/collections", HandleGetCollections)
            .WithDisplayName("OGC API Tiles Collections")
            .WithName("OgcTilesCollections")
            .WithSummary("Get OGC API Tiles collections")
            .WithDescription("Lists all available collections with tile links")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollections")
            .Produces<Collections>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        endpoints.MapGet("/ogc/tiles/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Tiles Collection")
            .WithName("OgcTilesCollection")
            .WithSummary("Get OGC API Tiles collection metadata")
            .WithDescription("Gets collection metadata with tile links")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollection")
            .Produces<CollectionInfo>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        return endpoints;
    }

    private static async Task<IResult> HandleGetCollections(
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog,
        ILogger<OgcTilesEndpoints.OgcTilesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var collections = layers.Select(layer => CreateCollection(layer, baseUrl)).ToImmutableArray();

            var links = OgcFeaturesUtilities.BuildFormatLinks(
                    request,
                    $"{baseUrl}/ogc/tiles/collections",
                    outputFormat,
                    OgcFeaturesUtilities.MetadataFormats,
                    "Collections")
                .ToBuilder();

            links.Add(Link.Create(
                href: $"{baseUrl}/ogc/tiles",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Landing page"));

            var response = new Collections
            {
                CollectionList = collections,
                Links = links.ToImmutable()
            };

            return OgcFeaturesUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.Collections, outputFormat, "Collections");
        }
        catch (Exception ex)
        {
            OgcTilesCollectionsEndpointLogging.LogCollectionsQueryFailed(logger, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500,
                "Internal server error",
                "An error occurred while retrieving collections.");
        }
    }

    private static async Task<IResult> HandleGetCollection(
        string collectionId,
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog,
        ILogger<OgcTilesEndpoints.OgcTilesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return OgcErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            if (!int.TryParse(collectionId, out var layerId))
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return OgcErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var collection = CreateCollection(layer, baseUrl);
            var basePath = $"{baseUrl}/ogc/tiles/collections/{collectionId}";
            var selfHref = $"{basePath}{request.QueryString}";
            var updatedLinks = collection.Links.Select(link =>
                    string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                        ? link with { Href = selfHref, Type = outputFormat }
                        : link)
                .ToImmutableArray();

            updatedLinks = OgcFeaturesUtilities.AddAlternateLinks(updatedLinks, request, basePath, outputFormat, OgcFeaturesUtilities.MetadataFormats);
            collection = collection with { Links = updatedLinks };

            return OgcFeaturesUtilities.FormatMetadataResponse(
                collection,
                OgcTilesJsonContext.Default.CollectionInfo,
                outputFormat,
                collection.Title ?? collection.Id);
        }
        catch (Exception ex)
        {
            OgcTilesCollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500,
                "Internal server error",
                "An error occurred while retrieving the collection.");
        }
    }

    private static CollectionInfo CreateCollection(LayerDefinition layer, string baseUrl)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var collectionLinks = ImmutableArray.Create(
            Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections/{collectionId}",
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: layer.Name
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Items,
                type: MediaTypes.GeoJson,
                title: "Items"
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Data,
                type: MediaTypes.GeoJson,
                title: "Data"
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles",
                rel: RelationTypes.TilesetsVector,
                type: MediaTypes.Json,
                title: "Vector tilesets"
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Collections"
            )
        );

        return new CollectionInfo
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            Links = collectionLinks,
            Extent = layer.Extent != null ? new Extent
            {
                Spatial = new SpatialExtent
                {
                    BoundingBox = ImmutableArray.Create(ImmutableArray.Create(
                        layer.Extent.Value.MinX,
                        layer.Extent.Value.MinY,
                        layer.Extent.Value.MaxX,
                        layer.Extent.Value.MaxY)),
                    Crs = layer.SpatialReference.Srid.ToOgcCrs()
                }
            } : null,
            Crs = ImmutableArray.Create(
                OgcFeaturesUtilities.Crs84Uri,
                OgcFeaturesUtilities.Epsg4326Uri
            ),
            StorageCrs = layer.SpatialReference.Srid.ToOgcCrs()
        };
    }

    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return OgcErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return OgcErrorHelpers.CreateBadRequest(context, "Invalid format.");
    }
}

internal static partial class OgcTilesCollectionsEndpointLogging
{
    [LoggerMessage(EventId = 5301, Level = LogLevel.Error,
        Message = "Failed to retrieve OGC Tiles collections.")]
    public static partial void LogCollectionsQueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Error,
        Message = "Failed to retrieve OGC Tiles collection {CollectionId}.")]
    public static partial void LogCollectionQueryFailed(ILogger logger, string collectionId, Exception exception);
}
