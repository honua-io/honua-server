// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class WellKnownCrsRegistry : ICrsRegistry
{
    private const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string EpsgUriPrefix = "http://www.opengis.net/def/crs/EPSG/0/";
    private const string EpsgUrnPrefix = "urn:ogc:def:crs:EPSG::";
    private const string EpsgPrefix = "EPSG:";

    // Same three definitions as PostgresCrsRegistry's built-in fast path, WKT included: a
    // DuckDB/MySQL-backed export has no catalog to fall back to, so a WKT-less definition
    // here means a shapefile with no .prj sidecar at all.
    private static readonly CrsDefinition _crs84Definition =
        new(Crs84Uri, 4326, AxisOrder.EastNorth, true) { Wkt = WellKnownCrsWkt.Epsg4326 };
    private static readonly CrsDefinition _epsg4326Definition =
        new($"{EpsgUriPrefix}4326", 4326, AxisOrder.NorthEast, true) { Wkt = WellKnownCrsWkt.Epsg4326 };
    private static readonly CrsDefinition _epsg3857Definition =
        new($"{EpsgUriPrefix}3857", 3857, AxisOrder.EastNorth, false) { Wkt = WellKnownCrsWkt.Epsg3857 };

    public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84Definition);
        }

        var normalized = Normalize(crsIdentifier);
        if (string.Equals(normalized, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84Definition);
        }

        return !TryParseSrid(normalized, out var srid)
            ? ValueTask.FromResult<CrsDefinition?>(null)
            : ResolveBySridAsync(srid, cancellationToken);
    }

    public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(srid switch
        {
            4326 => (CrsDefinition?)_epsg4326Definition,
            3857 => _epsg3857Definition,
            _ => null,
        });

    public async ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
        => (await ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false)).HasValue;

    public ValueTask<CoordinateSystemSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var search = CoordinateSystemSearchQuery.Parse(query, limit);
        var exactCode = search.ExactCode;
        query = search.Text;
        var items = new[] { _epsg4326Definition, _epsg3857Definition }
            .Select(CoordinateSystemEntry.FromDefinition)
            .OfType<CoordinateSystemEntry>()
            .Where(entry => entry.Code == exactCode || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Code == exactCode)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Code)
            .ToArray();
        return ValueTask.FromResult(new CoordinateSystemSearchResult { Items = items.Take(limit).ToArray(), HasMore = items.Length > limit });
    }

    private static string Normalize(string crsIdentifier)
    {
        var trimmed = crsIdentifier.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '<' && trimmed[^1] == '>')
        {
            trimmed = trimmed[1..^1];
        }

        if (string.Equals(trimmed, "CRS84", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "OGC:CRS84", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            return Crs84Uri;
        }

        if (trimmed.StartsWith(EpsgUrnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return EpsgUriPrefix + trimmed[EpsgUrnPrefix.Length..];
        }

        if (trimmed.StartsWith(EpsgPrefix, StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith(EpsgUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return EpsgUriPrefix + trimmed[EpsgPrefix.Length..];
        }

        return trimmed;
    }

    private static bool TryParseSrid(string normalizedIdentifier, out int srid)
    {
        srid = 0;

        if (normalizedIdentifier.StartsWith(EpsgUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                normalizedIdentifier[EpsgUriPrefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out srid);
        }

        return int.TryParse(normalizedIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
    }
}
