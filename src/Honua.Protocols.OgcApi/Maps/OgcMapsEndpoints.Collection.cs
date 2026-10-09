// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OgcCommon = Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Maps;

public static partial class OgcMapsEndpoints
{
    /// <summary>
    /// OGC API - Maps Part 1 (<c>/req/collection-map/desc-links</c>) names the map relation
    /// <see cref="OgcCommon.RelationTypes.Map"/> or its CURIE.
    /// </summary>
    private const string MapRelationCurie = "[ogc-rel:map]";

    /// <summary>
    /// The <c>http://</c> form of the map relation, as the OGC definitions server registers it.
    /// GDAL's OGCAPI driver (the provider QGIS uses for OGC API - Maps) takes the map URL only
    /// from a link with exactly this relation and a media type. Every other link, including the
    /// spec's <c>https://</c> form and the CURIE, lands in one fallback slot that the last link in
    /// the array overwrites, so without it GDAL requests the <c>alternate</c> HTML link as the map
    /// (#4991).
    /// </summary>
    private const string RegisteredMapRelation = "http://www.opengis.net/def/rel/ogc/1.0/map";

    /// <summary>
    /// Get the collection description for a collection that serves maps (OGC API - Maps
    /// Part 1, <c>/req/collection-map/desc-links</c>). Map clients such as GDAL read this
    /// resource before they request <c>/map</c>, so it resolves exactly when <c>/map</c> does.
    /// </summary>
    private static async Task<IResult> GetCollection(
        string collectionId,
        string? f,
        HttpContext context,
        [FromServices] ICoordinateTransformService coordinateTransformService,
        CancellationToken cancellationToken = default)
    {
        if (!OgcCommon.OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resolution = await ResolveCollectionAsync(context, collectionId, snapshot, cancellationToken);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var collection = await BuildCollectionInfoAsync(
            context, resolution.Resource!, resolution.Publication!, BuildMapCollectionIds(snapshot)[resolution.Publication!.Metadata.Id], outputFormat,
            coordinateTransformService, true, cancellationToken).ConfigureAwait(false);
        return OgcCommon.OgcCommonUtilities.FormatMetadataResponse(
            collection,
            OgcJsonContext.Default.CollectionInfo,
            outputFormat,
            collection.Title ?? collection.Id);
    }

    private static async Task<IResult> GetCollections(
        HttpContext context,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ICoordinateTransformService coordinateTransformService)
    {
        if (!OgcCommon.OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context, f, MetadataQueryParameters, out var outputFormat, out var errorResult))
        {
            return errorResult!;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var collections = ImmutableArray.CreateBuilder<OgcCommon.CollectionInfo>();
        var seenResources = new HashSet<string>(StringComparer.Ordinal);
        var collectionIds = BuildMapCollectionIds(snapshot);
        foreach (var publication in snapshot.Graph.Publications
                     .OrderByDescending(publication => ServiceProtocols.IsPreferredPublicationType(
                         ServiceProtocols.OgcApiMaps, publication.PublicationType))
                     .ThenByDescending(publication => publication.IsPrimary))
        {
            if (!snapshot.IsRoutable(publication) ||
                !snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                !ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.OgcApiMaps))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication)!;
            if (!TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service) ||
                !snapshot.ResolveStorageLayerId(publication, resource).HasValue)
            {
                continue;
            }

            var id = collectionIds[publication.Metadata.Id];
            var resolution = await ResolveCollectionAsync(context, id, snapshot, cancellationToken).ConfigureAwait(false);
            if (resolution.Error is null && resolution.Resource?.Metadata.Id == resource.Metadata.Id &&
                seenResources.Add(resource.Metadata.Id))
            {
                collections.Add(await BuildCollectionInfoAsync(
                    context, resolution.Resource!, resolution.Publication!, id, OgcCommon.MediaTypes.Json,
                    coordinateTransformService, false, cancellationToken).ConfigureAwait(false));
            }
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var collectionsPath = $"{baseUrl}/ogc/maps/collections";
        var links = ImmutableArray.Create(
            OgcCommon.Link.Create($"{collectionsPath}{context.Request.QueryString}", OgcCommon.RelationTypes.Self, outputFormat),
            OgcCommon.Link.Create(BaseUrlResolver.PreserveToken(context.Request, $"{baseUrl}/ogc/maps"), "parent", OgcCommon.MediaTypes.Json));
        links = OgcCommon.OgcCommonUtilities.AddAlternateLinks(
            links, context.Request, collectionsPath, outputFormat, OgcCommon.OgcCommonUtilities.MetadataFormats);
        var response = new OgcCommon.Collections
        {
            CollectionList = collections.OrderBy(collection => collection.Id, StringComparer.Ordinal).ToImmutableArray(),
            Links = links
        };
        return OgcCommon.OgcCommonUtilities.FormatMetadataResponse(
            response, OgcJsonContext.Default.Collections, outputFormat, "Map collections");
    }

    private static async Task<OgcCommon.CollectionInfo> BuildCollectionInfoAsync(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        string canonicalId,
        string outputFormat,
        ICoordinateTransformService coordinateTransformService,
        bool preserveQuery,
        CancellationToken cancellationToken)
    {
        var title = publication.TitleOverride
            ?? resource.Metadata.Title
            ?? resource.Metadata.Name;

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var collectionPath = $"{baseUrl}/ogc/maps/collections/{Uri.EscapeDataString(canonicalId)}";
        var mapHref = BaseUrlResolver.PreserveToken(context.Request, $"{collectionPath}/map");

        var links = ImmutableArray.Create(
            OgcCommon.Link.Create(
                href: preserveQuery ? $"{collectionPath}{context.Request.QueryString}" : BaseUrlResolver.PreserveToken(context.Request, collectionPath),
                rel: OgcCommon.RelationTypes.Self,
                type: outputFormat,
                title: title),
            OgcCommon.Link.Create(
                href: mapHref,
                rel: OgcCommon.RelationTypes.Map,
                type: OgcCommon.MediaTypes.Png,
                title: "Map"),
            OgcCommon.Link.Create(
                href: mapHref,
                rel: MapRelationCurie,
                type: OgcCommon.MediaTypes.Png,
                title: "Map"),
            OgcCommon.Link.Create(
                href: mapHref,
                rel: RegisteredMapRelation,
                type: OgcCommon.MediaTypes.Png,
                title: "Map"),
            OgcCommon.Link.Create(
                href: $"{baseUrl}/ogc/maps",
                rel: "parent",
                type: OgcCommon.MediaTypes.Json,
                title: "OGC API - Maps"));
        links = OgcCommon.OgcCommonUtilities.AddAlternateLinks(
            links,
            context.Request,
            collectionPath,
            outputFormat,
            OgcCommon.OgcCommonUtilities.MetadataFormats);

        return new OgcCommon.CollectionInfo
        {
            Id = canonicalId,
            Title = title,
            Description = resource.Metadata.Description,
            Links = links,
            Extent = await BuildCollectionExtentAsync(resource, coordinateTransformService, cancellationToken)
        };

    }

    private static Dictionary<string, string> BuildMapCollectionIds(MetadataV2GraphSnapshot snapshot)
    {
        var publications = snapshot.Graph.Publications.Where(candidate =>
            snapshot.IsRoutable(candidate) &&
            snapshot.Index.ServicesById.TryGetValue(candidate.ServiceId, out var service) &&
            ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.OgcApiMaps)).ToArray();
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var publication in publications)
        {
            var resource = snapshot.ResolveResource(publication)!;
            // Mirror the shared resolver's alias matching, including names and titles.
            foreach (var alias in new[] { publication.ServiceLocalId, publication.Path,
                         publication.Metadata.Name, publication.Metadata.Title, publication.Metadata.Id,
                         resource.Metadata.Name, resource.Metadata.Title, resource.Metadata.Id })
            {
                if (alias is null)
                {
                    continue;
                }

                if (!aliases.TryGetValue(alias, out var resources))
                {
                    resources = new HashSet<string>(StringComparer.Ordinal);
                    aliases.Add(alias, resources);
                }

                resources.Add(resource.Metadata.Id);
            }
        }

        // Exact publication IDs take precedence across the entire graph, including
        // other protocols and retired publications. Never emit another publication's
        // ID as this collection's alias.
        var publicationIds = snapshot.Graph.Publications.Select(publication => publication.Metadata.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return publications.ToDictionary(publication => publication.Metadata.Id, publication =>
        {
            var resource = snapshot.ResolveResource(publication)!;
            var alias = publication.ServiceLocalId ?? publication.Path ?? resource.Metadata.Name;
            var shadowsPublicationId = publicationIds.Contains(alias) &&
                !string.Equals(alias, publication.Metadata.Id, StringComparison.OrdinalIgnoreCase);
            return shadowsPublicationId || aliases[alias].Count > 1 ? publication.Metadata.Id : alias;
        }, StringComparer.Ordinal);
    }

    private static async Task<OgcCommon.Extent?> BuildCollectionExtentAsync(
        MetadataV2Resource resource,
        ICoordinateTransformService coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var bbox = resource.ReadBbox();
        if (bbox is null)
        {
            return null;
        }

        var crs84 = await OgcCommon.OgcExtentTransformer.TryTransformExtentToCrs84Async(
            bbox.West,
            bbox.South,
            bbox.East,
            bbox.North,
            resource.ReadSrid() ?? 4326,
            coordinateTransformService,
            cancellationToken).ConfigureAwait(false);
        if (!crs84.HasValue)
        {
            return null;
        }

        return new OgcCommon.Extent
        {
            Spatial = new OgcCommon.SpatialExtent
            {
                BoundingBox = ImmutableArray.Create(ImmutableArray.Create(
                    crs84.Value.MinLon,
                    crs84.Value.MinLat,
                    crs84.Value.MaxLon,
                    crs84.Value.MaxLat)),
                Crs = OgcFeaturesUtilities.Crs84Uri
            }
        };
    }
}
