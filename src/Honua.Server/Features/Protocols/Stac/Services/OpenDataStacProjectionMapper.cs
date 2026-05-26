// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.OpenData.Abstractions;
using Honua.Core.Features.OpenData.Domain;
using Honua.Server.Features.Infrastructure.OpenData.Services;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Stac.Models;

namespace Honua.Server.Features.Protocols.Stac.Services;

internal static class OpenDataStacProjectionMapper
{
    public static OpenDataPublicationService? TryResolveOpenDataPublicationService(HttpContext context)
    {
        if (context.RequestServices.GetService<IOpenDataStore>() is null)
        {
            return null;
        }

        return context.RequestServices.GetRequiredService<OpenDataPublicationService>();
    }

    public static StacCollection MapToCollection(
        OpenDataStacPublicationProjection projection,
        string baseUrl)
    {
        var record = projection.Record;
        var page = projection.Page;
        var stacBase = $"{baseUrl}/stac";
        var collectionUrl = $"{stacBase}/collections/{Uri.EscapeDataString(record.CollectionId)}";

        return new StacCollection
        {
            Id = record.CollectionId,
            Title = record.Title,
            Description = FirstNonEmpty(record.Description, record.Title, record.CollectionId)!,
            Keywords = page?.Tags.Count > 0 ? page.Tags.ToImmutableArray() : null,
            License = FirstNonEmpty(page?.License, "proprietary")!,
            Extent = BuildExtent(page),
            Links =
            [
                Link.Create(
                    href: collectionUrl,
                    rel: RelationTypes.Self,
                    type: MediaTypes.Json,
                    title: record.Title ?? record.CollectionId),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Root,
                    type: MediaTypes.Json,
                    title: "STAC Catalog"),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Parent,
                    type: MediaTypes.Json,
                    title: "STAC Catalog")
            ]
        };
    }

    public static Link MapToChildLink(
        OpenDataStacPublicationProjection projection,
        string stacBase)
    {
        var record = projection.Record;
        return Link.Create(
            href: $"{stacBase}/collections/{Uri.EscapeDataString(record.CollectionId)}",
            rel: StacConstants.StacRelations.Child,
            type: MediaTypes.Json,
            title: FirstNonEmpty(record.Title, record.CollectionId));
    }

    private static StacExtent BuildExtent(OpenDataPageRecord? page)
    {
        var spatial = page?.SpatialCoverage;
        var bbox = spatial is null
            ? ImmutableArray.Create(-180d, -90d, 180d, 90d)
            : ImmutableArray.Create(spatial.MinX, spatial.MinY, spatial.MaxX, spatial.MaxY);

        var temporal = page?.TemporalCoverage;
        var start = temporal?.Start?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var end = temporal?.End?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        return new StacExtent
        {
            Spatial = new StacSpatialExtent
            {
                Bbox = ImmutableArray.Create(bbox)
            },
            Temporal = new StacTemporalExtent
            {
                Interval = ImmutableArray.Create(ImmutableArray.Create<string?>(start, end))
            }
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
