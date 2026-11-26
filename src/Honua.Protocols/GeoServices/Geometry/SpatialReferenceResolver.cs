// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Infrastructure.Services;

internal class SpatialReferenceResolver
{
    private static readonly Regex _wktAuthorityNodeRegex = new(
        @"(?:AUTHORITY|ID)\s*\[\s*""EPSG""\s*,\s*""?(\d{3,6})""?\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] _wktRootKeywords =
    [
        "GEOGCS", "PROJCS", "COMPD_CS", "VERT_CS", "LOCAL_CS",
        "GEOGCRS", "GEOGRAPHICCRS", "PROJCRS", "PROJECTEDCRS",
        "GEODCRS", "GEODETICCRS", "COMPOUNDCRS",
        "VERTCRS", "VERTICALCRS", "LOCALCRS", "BOUNDCRS",
        "ENGCRS", "ENGINEERINGCRS", "PARAMETRICCRS", "TIMECRS", "TEMPORALCRS"
    ];

    private readonly ICrsDetectionService _crsDetectionService;
    private readonly ICrsRegistry _crsRegistry;

    public SpatialReferenceResolver(
        ICrsDetectionService crsDetectionService,
        ICrsRegistry crsRegistry)
    {
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
    }

    public async Task<int?> ResolveSridAsync(
        string? srValue,
        GeoServicesSpatialReference? geometrySpatialReference,
        CancellationToken cancellationToken = default)
    {
        var srid = await ParseSridAsync(srValue, cancellationToken).ConfigureAwait(false);
        if (srid.HasValue)
        {
            return await EnsureSupportedAsync(srid, cancellationToken).ConfigureAwait(false);
        }

        if (geometrySpatialReference != null)
        {
            srid = await ResolveSridFromGeometrySpatialReference(geometrySpatialReference, cancellationToken).ConfigureAwait(false);
            return await EnsureSupportedAsync(srid, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<int> ResolveGeodeticBaseSridAsync(
        int srid,
        CancellationToken cancellationToken = default)
    {
        var definition = await _crsRegistry.ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false);
        if (!definition.HasValue || definition.Value.IsGeographic)
        {
            return srid;
        }

        if (TryGetGeodeticBaseSridFromWkt(definition.Value.Wkt, out var baseSrid) ||
            TryGetGeodeticBaseSridFromProjJson(srid, out baseSrid))
        {
            return baseSrid;
        }

        return srid;
    }

    private static bool TryGetGeodeticBaseSridFromProjJson(int srid, out int baseSrid)
    {
        baseSrid = 0;
        if (!GeoParquetProjJsonCatalog.TryGetProjJson(srid, out var projJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(projJson);
        var root = document.RootElement;
        return root.TryGetProperty("base_crs", out var baseCrs) &&
               baseCrs.TryGetProperty("id", out var id) &&
               id.TryGetProperty("authority", out var authority) &&
               string.Equals(authority.GetString(), "EPSG", StringComparison.OrdinalIgnoreCase) &&
               id.TryGetProperty("code", out var code) &&
               code.TryGetInt32(out baseSrid);
    }

    private static bool TryGetGeodeticBaseSridFromWkt(string? wkt, out int baseSrid)
    {
        baseSrid = 0;
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return false;
        }

        foreach (var keyword in new[] { "BASEGEOGCRS", "BASEGEODCRS", "GEOGCS", "GEOGCRS", "GEODCRS" })
        {
            if (!TryExtractFirstWktMember(wkt, keyword, out var member))
            {
                continue;
            }

            var matches = _wktAuthorityNodeRegex.Matches(member);
            if (matches.Count > 0 &&
                int.TryParse(matches[^1].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out baseSrid))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractFirstWktMember(string wkt, string keyword, out string member)
    {
        member = string.Empty;
        var start = wkt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            var open = SkipWhitespace(wkt, start + keyword.Length);
            if (open < wkt.Length && wkt[open] == '[' && FindMatchingBracket(wkt, open) is var close && close > open)
            {
                member = wkt.Substring(start, close - start + 1);
                return true;
            }

            start = wkt.IndexOf(keyword, start + keyword.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int SkipWhitespace(string value, int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index;
    }

    private async Task<int?> EnsureSupportedAsync(int? srid, CancellationToken cancellationToken)
    {
        if (!srid.HasValue || srid.Value <= 0)
        {
            return null;
        }

        // Normalize well-known Web Mercator aliases (102100/102113/900913/3785) to the
        // canonical EPSG:3857 before registry validation. The registry only advertises the
        // canonical codes, so without this the query path rejected inSR/outSR/bboxSR=102100
        // that ArcGIS Pro and the ArcGIS JS API send, even though the edits and import paths
        // already normalize the same aliases. Returning the canonical SRID also lets
        // the shared query/transform pipeline short-circuit when the layer is stored as 3857
        // and the client requested a Web Mercator alias (no spurious ST_Transform).
        var normalized = SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid.Value);

        return await _crsRegistry.IsSridSupportedAsync(normalized, cancellationToken)
            .ConfigureAwait(false)
            ? normalized
            : null;
    }

    public async Task<int?> ParseSridAsync(string? srValue, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(srValue))
        {
            return null;
        }

        var trimmed = srValue.Trim();

        var parsedSrid = SpatialReferenceHelpers.TryParseSrid(trimmed);
        if (parsedSrid.HasValue)
        {
            return parsedSrid.Value;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid))
        {
            return srid;
        }

        if (trimmed.StartsWith('{'))
        {
            return await ParseSpatialReferenceJsonAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        var detected = _crsDetectionService.DetectFromEpsgCode(trimmed);
        if (detected.HasValue)
        {
            return detected.Value;
        }

        if (IsStructurallyValidWkt(trimmed))
        {
            return await _crsDetectionService.DetectFromWktAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<int?> ResolveSridFromGeometrySpatialReference(
        GeoServicesSpatialReference spatialRef,
        CancellationToken cancellationToken)
    {
        if (spatialRef.Wkid > 0)
        {
            return spatialRef.Wkid;
        }

        if (spatialRef.LatestWkid.HasValue)
        {
            return spatialRef.LatestWkid.Value;
        }

        if (!string.IsNullOrWhiteSpace(spatialRef.Wkt) && IsStructurallyValidWkt(spatialRef.Wkt))
        {
            return await _crsDetectionService.DetectFromWktAsync(spatialRef.Wkt, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<int?> ParseSpatialReferenceJsonAsync(string jsonValue, CancellationToken cancellationToken)
    {
        var parseResult = TryParseSpatialReferenceJson(jsonValue);
        if (!parseResult.Success)
        {
            return null;
        }

        if (parseResult.Wkid.HasValue)
        {
            return parseResult.Wkid.Value;
        }

        if (!string.IsNullOrWhiteSpace(parseResult.Name))
        {
            var epsg = _crsDetectionService.DetectFromEpsgCode(parseResult.Name);
            if (epsg.HasValue)
            {
                return epsg.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(parseResult.Wkt) && IsStructurallyValidWkt(parseResult.Wkt))
        {
            return await _crsDetectionService.DetectFromWktAsync(parseResult.Wkt, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static SpatialReferenceJsonParseResult TryParseSpatialReferenceJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return SpatialReferenceJsonParseResult.Failed;
            }

            // Skip unusable members so a supported fallback can still resolve the reference.
            var wkid = Int32Member(root, "wkid") ?? Int32Member(root, "latestWkid");
            var wkt = StringMember(root, "wkt");
            var name = StringMember(root, "name");

            var hasValidData = wkid.HasValue || !string.IsNullOrWhiteSpace(wkt) || !string.IsNullOrWhiteSpace(name);
            return new SpatialReferenceJsonParseResult(hasValidData, wkid, wkt, name);
        }
        catch (JsonException)
        {
            return SpatialReferenceJsonParseResult.Failed;
        }
    }

    private static int? Int32Member(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element)
           && element.ValueKind == JsonValueKind.Number
           && element.TryGetInt32(out var value)
            ? value
            : null;

    private static string? StringMember(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    internal static bool IsStructurallyValidWkt(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('\0') || !LooksLikeWkt(trimmed))
        {
            return false;
        }

        var openBracket = trimmed.IndexOf('[');
        return openBracket > 0
            && !HasDisallowedUnquotedWktToken(trimmed, openBracket)
            && FindMatchingBracket(trimmed, openBracket) == trimmed.Length - 1;
    }

    private static bool HasDisallowedUnquotedWktToken(string value, int startIndex)
    {
        var inQuote = false;
        for (var i = startIndex; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (!inQuote && IsDisallowedUnquotedTokenAt(value, i))
            {
                return true;
            }
        }

        return inQuote;
    }

    private static bool IsDisallowedUnquotedTokenAt(string value, int index)
        => value[index] is ';' or '\''
           || (value[index] == '-' && index + 1 < value.Length && value[index + 1] == '-');

    private static int FindMatchingBracket(string value, int openIndex)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = openIndex; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                continue;
            }

            depth += c == '[' ? 1 : c == ']' ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool LooksLikeWkt(string value)
        => _wktRootKeywords.Any(keyword => StartsWithWktKeyword(value, keyword));

    private static bool StartsWithWktKeyword(string value, string keyword)
    {
        if (!value.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var open = SkipWhitespace(value, keyword.Length);
        return open < value.Length && value[open] == '[';
    }

    private readonly record struct SpatialReferenceJsonParseResult(
        bool Success,
        int? Wkid,
        string? Wkt,
        string? Name)
    {
        public static readonly SpatialReferenceJsonParseResult Failed = new(false, null, null, null);
    }
}
