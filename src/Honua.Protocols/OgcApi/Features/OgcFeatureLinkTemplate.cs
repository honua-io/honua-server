// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Features;

// The per-response part of an OGC feature's links: base URL, escaped collection segment and
// the format and CRS query suffix of each representation. Built once per response, so each
// feature only escapes its id and concatenates its hrefs.
internal sealed class OgcFeatureLinkTemplate
{
    private string ItemsPrefix { get; init; } = string.Empty;

    private string SelfQuery { get; init; } = string.Empty;

    private string OutputFormat { get; init; } = string.Empty;

    private AlternateFormat[] Alternates { get; init; } = [];

    private Link CollectionLink { get; init; } = Link.Create(string.Empty);

    public static OgcFeatureLinkTemplate Create(HttpRequest request, OgcFeatureLinkScope scope)
    {
        var (collectionId, outputFormat, responseCrsUri) = scope;
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var collectionHref = $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(collectionId)}";
        var crsParameter = BuildCrsParameter(request, responseCrsUri);

        var alternates = new List<AlternateFormat>(OgcFeaturesUtilities.FeatureFormats.Length);
        foreach (var format in OgcFeaturesUtilities.FeatureFormats)
        {
            if (!string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                alternates.Add(new AlternateFormat(
                    BuildQuery(crsParameter, format.MediaType), format.MediaType, format.Title));
            }
        }

        return new OgcFeatureLinkTemplate
        {
            ItemsPrefix = collectionHref + "/items/",
            SelfQuery = BuildQuery(crsParameter, outputFormat),
            OutputFormat = outputFormat,
            Alternates = [.. alternates],
            CollectionLink = Link.Create(
                href: collectionHref,
                rel: RelationTypes.Collection,
                type: MediaTypes.Json,
                title: "Collection"),
        };
    }

    public ImmutableArray<Link> Build(string featureId)
    {
        var basePath = string.Concat(ItemsPrefix, Uri.EscapeDataString(featureId));
        var links = new Link[Alternates.Length + 2];
        links[0] = Link.Create(
            href: string.Concat(basePath, SelfQuery),
            rel: RelationTypes.Self,
            type: OutputFormat,
            title: "Feature");
        for (var i = 0; i < Alternates.Length; i++)
        {
            var alternate = Alternates[i];
            links[i + 1] = Link.Create(
                href: string.Concat(basePath, alternate.Query),
                rel: RelationTypes.Alternate,
                type: alternate.MediaType,
                title: alternate.Title);
        }

        // Link properties are init-only, so every feature shares the collection link.
        links[^1] = CollectionLink;
        return ImmutableCollectionsMarshal.AsImmutableArray(links);
    }

    private static string? BuildCrsParameter(HttpRequest request, string? responseCrsUri)
    {
        if (request.Query.TryGetValue("crs", out var crsValue) && !string.IsNullOrWhiteSpace(crsValue))
        {
            return $"crs={Uri.EscapeDataString(crsValue.ToString())}";
        }

        return string.IsNullOrWhiteSpace(responseCrsUri)
            ? null
            : $"crs={Uri.EscapeDataString(responseCrsUri)}";
    }

    private static string BuildQuery(string? crsParameter, string mediaType)
    {
        var formatQueryValue = OgcFeaturesUtilities.FeatureFormats
            .FirstOrDefault(format => string.Equals(format.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            .QueryValue;
        var formatParameter = string.IsNullOrWhiteSpace(formatQueryValue)
            ? null
            : $"f={Uri.EscapeDataString(formatQueryValue)}";

        return (crsParameter, formatParameter) switch
        {
            (null, null) => string.Empty,
            (not null, null) => "?" + crsParameter,
            (null, not null) => "?" + formatParameter,
            _ => $"?{crsParameter}&{formatParameter}",
        };
    }

    private readonly record struct AlternateFormat(string Query, string MediaType, string Title);
}

// What the links of one response point at: the collection, the response format and,
// when the request has no crs parameter, the CRS the response is in.
internal readonly record struct OgcFeatureLinkScope(
    string CollectionId,
    string OutputFormat,
    string? ResponseCrsUri = null);
