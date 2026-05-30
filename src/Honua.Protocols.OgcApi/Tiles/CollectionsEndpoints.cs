// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Ogc.Api.Tiles;

internal static class CollectionsEndpoints
{
    private const string OgcApiTilesProtocol = ServiceProtocols.OgcApiTiles;

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
        [FromServices] IMetadataV2GraphProvider graphProvider,
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
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            // Walk OGC API Tiles publications: each (resource, service) pair gated on
            // protocol enablement + access policy. Dedupe by resource (prefer IsPrimary).
            var byResource = new Dictionary<string, (MetadataV2Publication Publication, MetadataV2Service Service, MetadataV2Resource Resource)>(StringComparer.Ordinal);
            var requiresAuth = false;
            var hasDenied = false;
            var totalPublications = snapshot.Graph.Publications.Count;

            foreach (var publication in snapshot.Graph.Publications)
            {
                if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
                {
                    continue;
                }
                if (!ServiceProtocols.IsProtocolEnabled(service, OgcApiTilesProtocol))
                {
                    continue;
                }
                var resource = snapshot.ResolveResource(publication);
                if (resource is null)
                {
                    continue;
                }

                var decision = AccessPolicyHelpers.EvaluateAccess(
                    context,
                    resource.AccessPolicy,
                    service.AccessPolicy);

                if (decision.IsAllowed)
                {
                    if (!byResource.TryGetValue(resource.Metadata.Id, out var existing) ||
                        (publication.IsPrimary && !existing.Publication.IsPrimary))
                    {
                        byResource[resource.Metadata.Id] = (publication, service, resource);
                    }
                    continue;
                }

                hasDenied = true;
                if (decision.RequiresAuthentication)
                {
                    requiresAuth = true;
                }
            }

            var visible = byResource.Values
                .OrderBy(t => snapshot.ResolveStorageLayerId(t.Publication) ?? int.MaxValue)
                .ToArray();

            if (visible.Length == 0)
            {
                if (totalPublications == 0)
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

            var collections = new CollectionInfo[visible.Length];
            await Parallel.ForEachAsync(
                visible.Select((entry, index) => (entry, index)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(visible.Length, 8)
                },
                async (item, token) =>
                {
                    collections[item.index] = await CreateCollectionAsync(
                        item.entry.Resource,
                        item.entry.Publication,
                        snapshot,
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
        [FromServices] IMetadataV2GraphProvider graphProvider,
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
            var validation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context,
                collectionId,
                requiredProtocol: OgcApiTilesProtocol,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return validation.ErrorResult!;
            }

            var resource = validation.Resource!;
            var publication = validation.Publication!;
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            var collection = await CreateCollectionAsync(
                resource,
                publication,
                snapshot,
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

    private static async Task<CollectionInfo> CreateCollectionAsync(
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2GraphSnapshot snapshot,
        string baseUrl,
        IFeatureReader featureReader,
        ICoordinateTransformService coordinateTransformService,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        var collectionId = publication.ServiceLocalId
            ?? publication.Path
            ?? resource.Metadata.Name;
        var displayName = publication.TitleOverride
            ?? resource.Metadata.Title
            ?? resource.Metadata.Name;
        var escapedCollectionId = Uri.EscapeDataString(collectionId);

        var collectionLinks = ImmutableArray.Create(
            Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections/{escapedCollectionId}",
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: displayName
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{escapedCollectionId}/items",
                rel: RelationTypes.Items,
                type: MediaTypes.GeoJson,
                title: "Items"
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{escapedCollectionId}/items",
                rel: RelationTypes.Data,
                type: MediaTypes.GeoJson,
                title: "Data"
            ),
            Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections/{escapedCollectionId}/tiles",
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
        var bbox = resource.ReadBbox();
        if (bbox is not null)
        {
            var srid = resource.ReadSrid() ?? 4326;
            var transformedExtent = await OgcExtentTransformer.TryTransformExtentToCrs84Async(
                bbox.West,
                bbox.South,
                bbox.East,
                bbox.North,
                srid,
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

        TemporalExtent? temporalExtent = null;
        var storageLayerId = snapshot.ResolveStorageLayerId(publication);
        if (storageLayerId.HasValue)
        {
            temporalExtent = await OgcFeaturesUtilities.BuildTemporalExtentAsync(
                resource,
                storageLayerId.Value,
                featureReader,
                cancellationToken).ConfigureAwait(false);
        }

        var collectionExtent = spatialExtent == null && temporalExtent == null
            ? null
            : new Extent
            {
                Spatial = spatialExtent,
                Temporal = temporalExtent
            };

        CrsDefinition? storageCrsDefinition = null;
        var resourceSrid = resource.ReadSrid();
        if (resourceSrid.HasValue)
        {
            storageCrsDefinition = await crsRegistry.ResolveAsync(
                resourceSrid.Value.ToOgcCrs(),
                cancellationToken);
        }
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsUrisAsync(
            resource,
            crsRegistry,
            cancellationToken);

        return new CollectionInfo
        {
            Id = collectionId,
            Title = displayName,
            Description = resource.Metadata.Description,
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
