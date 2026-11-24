// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Infrastructure.SharedCaching;

// The CDN stores the tags a public response carries in the header named by SharedCache:CacheTagHeader, and an invalidation
// item "#tag" forgets every object carrying that tag. The CDN compares tags without regard to case, so one tag reaches every
// spelling of an address under which a response was stored, which a path prefix cannot, and a tag is not a wildcard path.
//
// Two kinds of tag: what a response is about (service, layer, style), and the address scope (the path prefix an edit would
// otherwise invalidate). Edits invalidate by scope, so a response drawn from several layers, or with no single subject, is
// still reached.
internal static class CdnCacheTags
{
    // The CDN accepts visible ASCII without commas, up to this many characters, and up to 50 tags per object; a response carries at most four.
    internal const int MaxTagLength = 256;

    internal const string InvalidationPrefix = "#";

    private const string Prefix = "honua-";

    // A name that would make a tag too long is replaced by a digest of itself, on the response and in the invalidation alike.
    private const int MaxNameLength = 96;

    // A GeoServices service is named by its resolved serviceId route value, the catalog name an edit knows, whatever folder or
    // alias the address used.
    internal static string[] ForResponse(SharedCacheSubject? subject, PathString path, string? resolvedServiceId = null)
    {
        var tags = new List<string>(4);
        if (subject is not null)
        {
            var service = Name(subject.ServiceId);
            tags.Add($"{Prefix}service:{service}");
            if (subject.LayerId is { } layerId)
            {
                tags.Add($"{Prefix}layer:{service}/{Name(layerId)}");
            }

            if (subject.StyleId is { } styleId)
            {
                tags.Add($"{Prefix}style:{service}/{Name(styleId)}");
            }
        }

        if (ScopeOf(path, resolvedServiceId) is { } scope)
        {
            tags.Add(scope);
        }

        return [.. tags];
    }

    // The same reach as the paths an edit invalidates without tags (CdnPaths.ForEditedLayer): the layer's own tiles, every OGC API
    // and STAC response (STAC collection extents are computed from the features), and every GeoServices response of each name
    // the layer's service is published as. Every edit path reaches STAC, whichever protocol carried the edit.
    internal static string[] InvalidationsForEditedLayer(int layerId, IEnumerable<string> serviceNames)
        =>
        [
            InvalidationPrefix + TilesScope(layerId.ToString(CultureInfo.InvariantCulture)),
            InvalidationPrefix + OgcScope,
            InvalidationPrefix + StacScope,
            .. serviceNames
                .Where(serviceName => !string.IsNullOrWhiteSpace(serviceName))
                .Select(Name)
                .Distinct(StringComparer.Ordinal)
                .Select(service => InvalidationPrefix + ServicesScope(service)),
        ];

    internal static string[] InvalidationsForEditedStorageLayer(int layerId) => InvalidationsForEditedLayer(layerId, []);

    internal static string[] InvalidationsForOgcSurface => [InvalidationPrefix + OgcScope];

    internal static bool IsTagInvalidation(string item) => item.StartsWith(InvalidationPrefix, StringComparison.Ordinal);

    private const string StacScope = Prefix + "scope:stac";

    private const string OgcScope = Prefix + "scope:ogc";

    private static string TilesScope(string layer) => $"{Prefix}scope:tiles/{layer}";

    private static string ServicesScope(string service) => $"{Prefix}scope:rest/services/{service}";

    private static string? ScopeOf(PathString path, string? resolvedServiceId)
    {
        var pathSpan = (path.Value ?? string.Empty).AsSpan();
        Span<Range> ranges = stackalloc Range[4];
        var count = pathSpan.Split(ranges, '/', StringSplitOptions.RemoveEmptyEntries);
        if (count == 0)
        {
            return null;
        }

        var first = pathSpan[ranges[0]];
        var isRestServices = count >= 2
            && first.Equals("rest", StringComparison.OrdinalIgnoreCase)
            && pathSpan[ranges[1]].Equals("services", StringComparison.OrdinalIgnoreCase);
        var geoServices = first.Equals("services", StringComparison.OrdinalIgnoreCase) || isRestServices;
        if (geoServices && !string.IsNullOrWhiteSpace(resolvedServiceId))
        {
            return ServicesScope(Name(resolvedServiceId));
        }

        if (first.Equals("stac", StringComparison.OrdinalIgnoreCase))
        {
            return StacScope;
        }

        if (first.Equals("ogc", StringComparison.OrdinalIgnoreCase))
        {
            return OgcScope;
        }

        if (count >= 2
            && first.Equals("tiles", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(pathSpan[ranges[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return TilesScope(layerId.ToString(CultureInfo.InvariantCulture));
        }

        if (isRestServices && count >= 3)
        {
            return ServicesScope(Name(pathSpan[ranges[2]].ToString()));
        }

        return null;
    }

    // Lower-cased so the tag is the same whichever spelling the route was given; escaping leaves only visible ASCII without commas.
    private static string Name(string value)
    {
        var escaped = Uri.EscapeDataString(value.Trim().ToLowerInvariant());
        return escaped.Length <= MaxNameLength
            ? escaped
            : "~" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(escaped)))[..32];
    }
}
