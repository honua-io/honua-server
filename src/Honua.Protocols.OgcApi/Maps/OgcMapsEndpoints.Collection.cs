// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Features;
using Microsoft.AspNetCore.Http;
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
        ICoordinateTransformService coordinateTransformService,
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
        var resolution = await ResolveCollectionAsync(context, collectionId, cancellationToken);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var resource = resolution.Resource!;
        var publication = resolution.Publication!;
        var canonicalId = publication.ServiceLocalId
            ?? publication.Path
            ?? resource.Metadata.Name;
        var title = publication.TitleOverride
            ?? resource.Metadata.Title
            ?? resource.Metadata.Name;

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var collectionPath = $"{baseUrl}/ogc/maps/collections/{Uri.EscapeDataString(canonicalId)}";
        var mapHref = $"{collectionPath}/map";

        var links = ImmutableArray.Create(
            OgcCommon.Link.Create(
                href: $"{collectionPath}{context.Request.QueryString}",
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

        var collection = new OgcCommon.CollectionInfo
        {
            Id = canonicalId,
            Title = title,
            Description = resource.Metadata.Description,
            Links = links,
            Extent = await BuildCollectionExtentAsync(resource, coordinateTransformService, cancellationToken)
        };

        return OgcCommon.OgcCommonUtilities.FormatMetadataResponse(
            collection,
            OgcJsonContext.Default.CollectionInfo,
            outputFormat,
            title);
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
