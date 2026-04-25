// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Ogc.Api.Tiles;

internal static class CollectionsEndpoints
{
    private const string OgcApiTilesProtocol = "OGC-API-Tiles";

    public static IEndpointRouteBuilder MapCollectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var collections = endpoints.MapGet("/ogc/tiles/collections", HandleGetCollections)
            .WithDisplayName("OGC API Tiles Collections")
            .WithName("OgcTilesCollections")
            .WithSummary("Get OGC API Tiles collections")
            .WithDescription("Lists all available collections with tile links")
            .WithTags("OGC API Tiles")
            .CacheOutput("OgcTilesCollections")
            .Produces<Collections>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html);

        var collection = endpoints.MapGet("/ogc/tiles/collections/{collectionId}", HandleGetCollection)
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
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICoordinateTransformService coordinateTransformService,
        [FromServices] ICrsRegistry crsRegistry,
        [FromServices] ILogger<OgcTilesCollectionsLog> logger)
    {
        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        try
        {
            var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var services = await layerCatalog.ListServicesAsync(cancellationToken);
            var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services, OgcApiTilesProtocol);
            var visibleLayers = new List<LayerDefinition>(layers.Length);
            var requiresAuth = false;
            var hasDenied = false;

            foreach (var layer in layers)
            {
                var service = GetPrimaryService(layer.Id, primaryServices);
                if (!IsOgcApiTilesEnabled(layer, service))
                {
                    continue;
                }

                var decision = AccessPolicyHelpers.EvaluateAccess(
                    context,
                    layer.Metadata?.AccessPolicy,
                    service?.Metadata?.AccessPolicy);

                if (decision.IsAllowed)
                {
                    visibleLayers.Add(layer);
                    continue;
                }

                hasDenied = true;
                if (decision.RequiresAuthentication)
                {
                    requiresAuth = true;
                }
            }

            if (visibleLayers.Count == 0)
            {
                if (layers.Length == 0)
                {
                    return StandardErrorHelpers.CreateNotFound(context, "No collections are available.");
                }

                if (hasDenied)
                {
                    return requiresAuth
                        ? StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage)
                        : StandardErrorHelpers.CreateForbidden(context, AccessPolicyHelpers.AccessForbiddenMessage);
                }

                return StandardErrorHelpers.CreateNotFound(context, "No collections are available.");
            }

            var collections = new CollectionInfo[visibleLayers.Count];
            await Parallel.ForEachAsync(
                visibleLayers.Select((layer, index) => (layer, index)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(visibleLayers.Count, 8)
                },
                async (item, token) =>
                {
                    collections[item.index] = await CreateCollectionAsync(
                        item.layer,
                        baseUrl,
                        featureReader,
                        coordinateTransformService,
                        crsRegistry,
                        token);
                });

            var links = OgcCommonUtilities.BuildFormatLinks(
                    request,
                    $"{baseUrl}/ogc/tiles/collections",
                    outputFormat,
                    OgcCommonUtilities.MetadataFormats,
                    "Collections")
                .ToBuilder();

            links.Add(Link.Create(
                href: $"{baseUrl}/ogc/tiles",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Landing page"));

            var response = new Collections
            {
                CollectionList = collections.ToImmutableArray(),
                Links = links.ToImmutable()
            };

            return OgcCommonUtilities.FormatMetadataResponse(response, OgcTilesJsonContext.Default.Collections, outputFormat, "Collections");
        }
        catch (OperationCanceledException)
            when (OgcTilesUtilities.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcTilesCollectionsEndpointLogging.LogCollectionsQueryFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving collections.");
        }
    }

    private static async Task<IResult> HandleGetCollection(
        string collectionId,
        HttpContext context,
        string? f,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICoordinateTransformService coordinateTransformService,
        [FromServices] ICrsRegistry crsRegistry,
        [FromServices] ILogger<OgcTilesCollectionsLog> logger)
    {
        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        try
        {
            var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcTilesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var cancellationToken = OgcTilesUtilities.GetTimeoutAwareCancellationToken(context);
            var layer = await ResolveCollectionLayerAsync(context, collectionId, cancellationToken);
            if (layer == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var services = await layerCatalog.ListServicesAsync(cancellationToken);
            var primaryService = GetPrimaryService(layer.Id, LayerValidationHelpers.BuildPrimaryServiceMap(services, OgcApiTilesProtocol));
            if (!IsOgcApiTilesEnabled(layer, primaryService))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, primaryService);
            if (accessError != null)
            {
                return accessError;
            }

            var collection = await CreateCollectionAsync(
                layer,
                baseUrl,
                featureReader,
                coordinateTransformService,
                crsRegistry,
                cancellationToken);
            var encodedCollectionId = Uri.EscapeDataString(collectionId);
            var basePath = $"{baseUrl}/ogc/tiles/collections/{encodedCollectionId}";
            var selfHref = $"{basePath}{request.QueryString}";
            var updatedLinks = collection.Links.Select(link =>
                    string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                        ? link with { Href = selfHref, Type = outputFormat }
                        : link)
                .ToImmutableArray();

            updatedLinks = OgcCommonUtilities.AddAlternateLinks(updatedLinks, request, basePath, outputFormat, OgcCommonUtilities.MetadataFormats);
            collection = collection with { Links = updatedLinks };

            return OgcCommonUtilities.FormatMetadataResponse(
                collection,
                OgcTilesJsonContext.Default.CollectionInfo,
                outputFormat,
                collection.Title ?? collection.Id);
        }
        catch (OperationCanceledException)
            when (OgcTilesUtilities.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcTilesCollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the collection.");
        }
    }

    private static ServiceDefinition? GetPrimaryService(
        int layerId,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
        => primaryServices.TryGetValue(layerId, out var service) ? service : null;

    private static bool IsOgcApiTilesEnabled(LayerDefinition layer, ServiceDefinition? service)
    {
        var metadata = service?.Metadata ?? layer.Metadata;
        return ServiceProtocols.IsProtocolEnabled(metadata, OgcApiTilesProtocol);
    }

    private static async Task<LayerDefinition?> ResolveCollectionLayerAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await resourceValidator.ValidateCollectionAsync(collectionId, cancellationToken);
        return validationResult.IsValid ? validationResult.Resource : null;
    }

    private static async Task<CollectionInfo> CreateCollectionAsync(
        LayerDefinition layer,
        string baseUrl,
        IFeatureReader featureReader,
        ICoordinateTransformService coordinateTransformService,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
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

        SpatialExtent? spatialExtent = null;
        if (layer.Extent != null)
        {
            var layerExtent = layer.Extent.Value;
            var transformedExtent = await OgcExtentTransformer.TryTransformExtentToCrs84Async(
                layerExtent.MinX,
                layerExtent.MinY,
                layerExtent.MaxX,
                layerExtent.MaxY,
                layerExtent.SpatialReference,
                coordinateTransformService,
                cancellationToken);

            if (transformedExtent.HasValue)
            {
                spatialExtent = new SpatialExtent
                {
                    BoundingBox = ImmutableArray.Create(ImmutableArray.Create(
                        transformedExtent.Value.MinLon,
                        transformedExtent.Value.MinLat,
                        transformedExtent.Value.MaxLon,
                        transformedExtent.Value.MaxLat)),
                    Crs = OgcFeaturesUtilities.Crs84Uri
                };
            }
        }

        var temporalExtent = await OgcFeaturesUtilities.BuildTemporalExtentAsync(layer, featureReader, cancellationToken);
        var collectionExtent = spatialExtent == null && temporalExtent == null
            ? null
            : new Extent
            {
                Spatial = spatialExtent,
                Temporal = temporalExtent
            };

        var storageCrsDefinition = await crsRegistry.ResolveAsync(
            layer.SpatialReference.ToOgcCrs(),
            cancellationToken);
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsUrisAsync(
            layer,
            crsRegistry,
            cancellationToken);

        return new CollectionInfo
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            Links = collectionLinks,
            Extent = collectionExtent,
            Crs = supportedCrs,
            StorageCrs = storageCrsDefinition?.Uri
        };
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

/// <summary>
/// Logger category for OGC Tiles collections endpoints.
/// </summary>
internal sealed class OgcTilesCollectionsLog
{
}
