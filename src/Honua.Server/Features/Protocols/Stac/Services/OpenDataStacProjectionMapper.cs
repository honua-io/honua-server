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
        var links = ImmutableArray.CreateBuilder<Link>();
        links.Add(Link.Create(
            href: collectionUrl,
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: record.Title ?? record.CollectionId));
        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Root,
            type: MediaTypes.Json,
            title: "STAC Catalog"));
        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Parent,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        var licenseUri = ResolveLicenseUri(page?.License);
        if (licenseUri is not null)
        {
            links.Add(Link.Create(
                href: licenseUri,
                rel: "license",
                type: MediaTypes.Html,
                title: "License"));
        }

        return new StacCollection
        {
            Id = record.CollectionId,
            Title = record.Title,
            Description = FirstNonEmpty(record.Description, record.Title, record.CollectionId)!,
            Keywords = page?.Tags.Count > 0 ? page.Tags.ToImmutableArray() : null,
            License = ResolveStacLicense(page?.License),
            Extent = BuildExtent(page),
            Links = links.ToImmutable()
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

    private static string ResolveStacLicense(string? license)
    {
        var value = FirstNonEmpty(license);
        if (value is null)
        {
            return "proprietary";
        }

        if (TryMapLicenseUriToSpdx(value, out var spdxIdentifier))
        {
            return spdxIdentifier;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out _)
            ? "proprietary"
            : value;
    }

    private static string? ResolveLicenseUri(string? license)
    {
        var value = FirstNonEmpty(license);
        return value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private static bool TryMapLicenseUriToSpdx(string value, out string spdxIdentifier)
    {
        spdxIdentifier = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        spdxIdentifier = normalized switch
        {
            "http://creativecommons.org/licenses/by/4.0" or
            "https://creativecommons.org/licenses/by/4.0" => "CC-BY-4.0",
            "http://creativecommons.org/licenses/by-sa/4.0" or
            "https://creativecommons.org/licenses/by-sa/4.0" => "CC-BY-SA-4.0",
            "http://creativecommons.org/licenses/by-nc/4.0" or
            "https://creativecommons.org/licenses/by-nc/4.0" => "CC-BY-NC-4.0",
            "http://creativecommons.org/publicdomain/zero/1.0" or
            "https://creativecommons.org/publicdomain/zero/1.0" => "CC0-1.0",
            "http://opensource.org/licenses/mit" or
            "https://opensource.org/licenses/mit" or
            "http://opensource.org/license/mit" or
            "https://opensource.org/license/mit" => "MIT",
            _ => string.Empty
        };

        return spdxIdentifier.Length > 0;
    }
}
