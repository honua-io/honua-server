// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

/// <summary>
/// Service for detecting coordinate reference systems from various sources
/// Behavior reference: ../Honua.Server/src/platform/core/Query/Filter/CqlFilterParser.cs
/// Implements CRS detection from .prj files, WKT, EPSG codes, and GeoJSON
/// </summary>
internal sealed partial class CrsDetectionService : ICrsDetectionService
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<CrsDetectionService> _logger;

    /// <summary>
    /// Common EPSG codes and their variations for quick lookup
    /// </summary>
    private static readonly FrozenDictionary<string, int> _wellKnownEpsgCodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // WGS84 variants (including ArcGIS-style names)
            ["WGS84"] = 4326,
            ["WGS_84"] = 4326,
            ["WGS 84"] = 4326,
            ["WGS_1984"] = 4326,
            ["GCS_WGS_1984"] = 4326,
            ["D_WGS_1984"] = 4326,
            ["EPSG:4326"] = 4326,
            ["4326"] = 4326,

            // Web Mercator variants
            ["EPSG:3857"] = 3857,
            ["3857"] = 3857,
            ["GOOGLE_MERCATOR"] = 3857,
            ["WEB_MERCATOR"] = 3857,
            ["PSEUDO_MERCATOR"] = 3857,

            // NAD83 variants (including ArcGIS-style names)
            ["NAD83"] = 4269,
            ["NAD_83"] = 4269,
            ["NAD_1983"] = 4269,
            ["GCS_North_American_1983"] = 4269,
            ["D_North_American_1983"] = 4269,
            ["EPSG:4269"] = 4269,
            ["4269"] = 4269,
        }
        .ToFrozenDictionary();

    /// <summary>
    /// Regex patterns for extracting EPSG codes from various string formats
    /// </summary>
    private static readonly Regex _epsgCodeRegex = new(
        @"(?:EPSG:|AUTHORITY\[""EPSG"",""?)(\d{3,6})(?:""|])?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the EPSG code from a WKT authority node in either WKT1
    /// (<c>AUTHORITY["EPSG","25832"]</c>) or WKT2 / PROJ-6 (<c>ID["EPSG",25832]</c>) form.
    /// Detection runs against a single CRS scope (see <see cref="ScopeToPrimaryCrs"/>): for a plain
    /// PROJCS/PROJCRS the projected authority node is the <em>last</em> match in the string and,
    /// for projected WKT, one that sits <em>after</em> the inner base-geographic block — so a
    /// projected CRS is not misread as its base geographic CRS, and WKT2 <c>ID[]</c> nodes
    /// (PROJ-6 <c>.prj</c>, FlatGeobuf) are honored. Compound (COMPD_CS/COMPOUNDCRS) and bound
    /// (BOUNDCRS) WKT are scoped to their horizontal / source CRS member before this runs so a
    /// trailing vertical (5703-family) or transform authority never wins (#2743).
    /// </summary>
    private static readonly Regex _wktAuthorityNodeRegex = new(
        @"(?:AUTHORITY|ID)\s*\[\s*""EPSG""\s*,\s*""?(\d{3,6})""?\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the projected CRS name from the opening of a <c>PROJCS[...]</c> (WKT1) or
    /// <c>PROJCRS[...]</c> (WKT2) node so common ESRI projected names can be matched when the
    /// file carries no AUTHORITY/ID node (typical of ArcGIS-authored <c>.prj</c> sidecars).
    /// </summary>
    private static readonly Regex _projectedNameRegex = new(
        @"PROJC(?:S|RS)\s*\[\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches ESRI UTM projected-CRS names (e.g. <c>NAD_1983_UTM_Zone_17N</c>,
    /// <c>WGS_1984_UTM_Zone_32N</c>, <c>ETRS_1989_UTM_Zone_32N</c>) so the EPSG code can be
    /// computed from the datum + zone + hemisphere. Datum realizations (e.g. <c>NAD_1983_HARN</c>,
    /// <c>NAD_1983_2011</c>, <c>NAD_1983_CSRS</c>) are deliberately NOT matched here: their zone
    /// arithmetic differs from plain NAD83 (26900+zone), so mapping <c>NAD_1983_HARN_UTM_Zone_17N</c>
    /// through this branch would silently pick the wrong datum. Realization names fall through to
    /// the spatial_ref_sys WKT match (or an explicit source SRID) instead (#2743).
    /// </summary>
    private static readonly Regex _esriUtmNameRegex = new(
        @"^(?<datum>WGS_1984|NAD_1983|ETRS_1989|ETRS89)_UTM_Zone_(?<zone>\d{1,2})(?<hemi>[NS])$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Small, deliberately-limited table of common ESRI projected-CRS names to EPSG codes for
    /// files that carry no authority node. Full PROJ WKT parsing is a non-goal (#2743); UTM
    /// families are handled by <see cref="_esriUtmNameRegex"/> instead of enumerating them here.
    /// </summary>
    private static readonly FrozenDictionary<string, int> _esriProjectedNameCodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["WGS_1984_Web_Mercator_Auxiliary_Sphere"] = 3857,
            ["WGS_1984_Web_Mercator"] = 3857,
            ["WGS_1984_World_Mercator"] = 3395,
        }
        .ToFrozenDictionary();

    public CrsDetectionService(IAdoNetDatabaseConnectionProvider connectionProvider, ILogger<CrsDetectionService> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<int?> DetectFromPrjAsync(string prjContent, CancellationToken cancellationToken = default)
    {
        // A .prj sidecar is a single WKT string (WKT1 or WKT2 / PROJ-6). The WKT detection
        // path handles authority/ID nodes, ESRI projected names, well-known names, and the
        // spatial_ref_sys fallback, so .prj detection delegates to it rather than duplicating
        // (and re-running) the same logic.
        if (string.IsNullOrWhiteSpace(prjContent))
            return Task.FromResult<int?>(null);

        return DetectFromWktAsync(prjContent.Trim(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromWktAsync(string wktContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wktContent))
            return null;

        var cleanedContent = wktContent.Trim();

        // Scope compound/bound WKT to the CRS that actually describes the data's horizontal
        // coordinates before extracting any authority code (#2743). A COMPD_CS/COMPOUNDCRS carries
        // a trailing vertical CRS whose authority (5703-family) also validates in spatial_ref_sys,
        // and a BOUNDCRS carries TARGETCRS/transform authorities — taking the "last" node from the
        // whole string would resolve to the wrong code. Plain PROJCS/GEOGCS pass through unchanged.
        cleanedContent = ScopeToPrimaryCrs(cleanedContent);

        // Projected CRS need special care: the authoritative code is the OUTER projected authority
        // node, which appears AFTER the inner base-geographic block. A code taken from inside the
        // GEOGCS/GEOGCRS/BASEGEOGCRS block (or a first-match bare EPSG reference) is the base
        // geographic code (4326/4269-family) and would georeference projected data as lat/lon.
        var isProjected = cleanedContent.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) ||
                          cleanedContent.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase);
        var geographicBlockEnd = isProjected ? FindGeographicBaseBlockEnd(cleanedContent) : -1;

        // 1. Authority/ID node extraction. Take the LAST AUTHORITY["EPSG",...]/ID["EPSG",...]
        //    match — the outermost node within the scoped CRS — so a projected CRS with an
        //    authority node on both its base geographic CRS and itself resolves to the projected
        //    code, and so WKT2 ID[] nodes (PROJ-6 .prj / FlatGeobuf) are honored. For projected
        //    WKT, reject a last node that falls inside the base-geographic block: it is the base
        //    geographic code, not the projected one, and must never be accepted for projected data.
        var nodeMatches = _wktAuthorityNodeRegex.Matches(cleanedContent);
        if (nodeMatches.Count > 0)
        {
            var lastNode = nodeMatches[^1];
            var isBaseGeographicNode = isProjected && lastNode.Index <= geographicBlockEnd;
            if (!isBaseGeographicNode
                && int.TryParse(lastNode.Groups[1].Value, out var nodeEpsg)
                && await ValidateSridAsync(nodeEpsg, cancellationToken))
            {
                return nodeEpsg;
            }
        }

        // 2. Bare "EPSG:NNNN" reference (rare inside WKT but cheap to check). Only trusted for
        //    non-projected WKT: for projected WKT the first match grabs the inner base-geographic
        //    authority (4326-family) and would mis-georeference the data (#2743), so projected WKT
        //    skips straight to ESRI-name / spatial_ref_sys matching or an explicit source SRID.
        if (!isProjected)
        {
            var epsgMatch = _epsgCodeRegex.Match(cleanedContent);
            if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var epsgCode)
                && await ValidateSridAsync(epsgCode, cancellationToken))
            {
                return epsgCode;
            }
        }

        // Check for well-known coordinate system names, but only when the WKT
        // is not a projected CRS to avoid false matches on datum names
        if (!isProjected)
        {
            foreach (var kvp in _wellKnownEpsgCodes.Where(kvp => cleanedContent.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)))
            {
                if (await ValidateSridAsync(kvp.Value, cancellationToken))
                    return kvp.Value;
            }
        }
        else if (TryMatchEsriProjectedName(cleanedContent, out var esriEpsg)
                 && await ValidateSridAsync(esriEpsg, cancellationToken))
        {
            // 3. Common ESRI projected .prj files carry no authority node; match the projected
            //    CRS name (Web Mercator, UTM families) to an EPSG code.
            return esriEpsg;
        }

        // 4. Try to match against PostGIS spatial_ref_sys by WKT comparison
        return await FindSridByWktMatchAsync(cleanedContent, cancellationToken);
    }

    /// <summary>
    /// Attempts to map a common ESRI projected-CRS WKT name to its EPSG code without full PROJ
    /// parsing. Handles a small explicit table (Web Mercator, World Mercator) and the WGS 84 /
    /// NAD83 / ETRS89 UTM zone families. Returns <see langword="false"/> when the name is not a
    /// recognized ESRI projected name, in which case detection falls through to the
    /// spatial_ref_sys WKT match (or the caller must supply an explicit source SRID).
    /// </summary>
    private static bool TryMatchEsriProjectedName(string wktContent, out int epsg)
    {
        epsg = 0;

        var nameMatch = _projectedNameRegex.Match(wktContent);
        if (!nameMatch.Success)
            return false;

        var name = nameMatch.Groups[1].Value.Trim();

        if (_esriProjectedNameCodes.TryGetValue(name, out epsg))
            return true;

        var utm = _esriUtmNameRegex.Match(name);
        if (!utm.Success)
            return false;

        if (!int.TryParse(utm.Groups["zone"].Value, out var zone) || zone is < 1 or > 60)
            return false;

        var north = string.Equals(utm.Groups["hemi"].Value, "N", StringComparison.OrdinalIgnoreCase);
        var datum = utm.Groups["datum"].Value;

        if (datum.StartsWith("WGS_1984", StringComparison.OrdinalIgnoreCase))
        {
            epsg = (north ? 32600 : 32700) + zone;
            return true;
        }

        if (datum.StartsWith("NAD_1983", StringComparison.OrdinalIgnoreCase) && north)
        {
            // NAD83 UTM (northern hemisphere): EPSG 26901..26923 for zones 1..23.
            epsg = 26900 + zone;
            return true;
        }

        if ((datum.StartsWith("ETRS_1989", StringComparison.OrdinalIgnoreCase)
                || datum.StartsWith("ETRS89", StringComparison.OrdinalIgnoreCase))
            && north)
        {
            // ETRS89 UTM: EPSG 25828..25838 for zones 28..38.
            epsg = 25800 + zone;
            return true;
        }

        epsg = 0;
        return false;
    }

    /// <summary>
    /// Narrows a WKT string to the single CRS whose authority code describes the data's horizontal
    /// coordinates, before any authority extraction runs (#2743):
    /// <list type="bullet">
    /// <item>COMPD_CS / COMPOUNDCRS → the first projected (PROJCS/PROJCRS) member, else the first
    /// geographic (GEOGCS/GEOGCRS) member. This avoids resolving to the trailing vertical CRS,
    /// whose EPSG authority (e.g. 5703) also validates in <c>spatial_ref_sys</c>.</item>
    /// <item>BOUNDCRS → the SOURCECRS member. This avoids resolving to TARGETCRS or the abridged
    /// transformation, which carry different (transform/target) authority codes.</item>
    /// <item>Plain PROJCS/GEOGCS (and anything unrecognized) is returned unchanged.</item>
    /// </list>
    /// </summary>
    private static string ScopeToPrimaryCrs(string wkt)
    {
        if (StartsWithKeyword(wkt, "COMPD_CS") || StartsWithKeyword(wkt, "COMPOUNDCRS"))
        {
            return ExtractFirstMember(wkt, "PROJCRS")
                ?? ExtractFirstMember(wkt, "PROJCS")
                ?? ExtractFirstMember(wkt, "GEOGCRS")
                ?? ExtractFirstMember(wkt, "GEOGCS")
                ?? wkt;
        }

        if (StartsWithKeyword(wkt, "BOUNDCRS"))
        {
            return ExtractFirstMember(wkt, "SOURCECRS") ?? wkt;
        }

        return wkt;
    }

    /// <summary>
    /// Returns the index of the closing bracket of the first base-geographic block
    /// (GEOGCS / BASEGEOGCRS / GEOGCRS) in the WKT, or <c>-1</c> when none is present. Used to
    /// decide whether an authority node sits inside the base-geographic member of a projected CRS.
    /// </summary>
    private static int FindGeographicBaseBlockEnd(string wkt)
    {
        if (TryFindMember(wkt, "GEOGCS", out _, out var wkt1End))
        {
            return wkt1End;
        }

        if (TryFindMember(wkt, "BASEGEOGCRS", out _, out var wkt2BaseEnd))
        {
            return wkt2BaseEnd;
        }

        return TryFindMember(wkt, "GEOGCRS", out _, out var wkt2End) ? wkt2End : -1;
    }

    private static bool StartsWithKeyword(string wkt, string keyword) =>
        wkt.AsSpan().TrimStart().StartsWith(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the substring of the first <paramref name="keyword"/>[...] member of the WKT via a
    /// balanced-bracket scan, or <see langword="null"/> when the keyword is not present as a node.
    /// </summary>
    private static string? ExtractFirstMember(string wkt, string keyword) =>
        TryFindMember(wkt, keyword, out var start, out var end)
            ? wkt.Substring(start, end - start + 1)
            : null;

    /// <summary>
    /// Finds the first <paramref name="keyword"/>[...] node in the WKT and reports the index of the
    /// keyword and of its matching closing bracket via a balanced-bracket scan that ignores brackets
    /// inside quoted strings.
    /// </summary>
    private static bool TryFindMember(string wkt, string keyword, out int start, out int end)
    {
        start = -1;
        end = -1;

        var idx = wkt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var i = idx + keyword.Length;
            while (i < wkt.Length && char.IsWhiteSpace(wkt[i]))
            {
                i++;
            }

            if (i < wkt.Length && wkt[i] == '[')
            {
                var close = FindMatchingBracket(wkt, i);
                if (close > i)
                {
                    start = idx;
                    end = close;
                    return true;
                }
            }

            idx = wkt.IndexOf(keyword, idx + keyword.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Returns the index of the <c>]</c> matching the <c>[</c> at <paramref name="openIndex"/>, or
    /// <c>-1</c> when the brackets are unbalanced. Brackets inside double-quoted strings are ignored.
    /// </summary>
    private static int FindMatchingBracket(string s, int openIndex)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = openIndex; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                continue;
            }

            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public int? DetectFromEpsgCode(string epsgCode)
    {
        if (string.IsNullOrWhiteSpace(epsgCode))
            return null;

        // Remove common prefixes and clean up
        var cleaned = epsgCode.Trim()
            .Replace("EPSG:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("SRID=", "", StringComparison.OrdinalIgnoreCase);

        // Validate reasonable EPSG/SRID range (includes user-defined SRIDs up to 999999)
        if (int.TryParse(cleaned, out var srid) && srid > 0 && srid <= 999999)
        {
            return srid;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromGeoJsonCrsAsync(string crsObject, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crsObject))
            return null;

        try
        {
            using var document = JsonDocument.Parse(crsObject);
            var root = document.RootElement;

            // GeoJSON CRS format: {"type": "name", "properties": {"name": "EPSG:4326"}}
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() == "name" &&
                root.TryGetProperty("properties", out var propertiesElement) &&
                propertiesElement.TryGetProperty("name", out var nameElement))
            {
                var crsName = nameElement.GetString();
                if (!string.IsNullOrEmpty(crsName))
                {
                    return DetectFromEpsgCode(crsName);
                }
            }

            // Alternative format with direct EPSG reference
            if (root.TryGetProperty("name", out var directNameElement))
            {
                var crsName = directNameElement.GetString();
                if (!string.IsNullOrEmpty(crsName))
                {
                    return DetectFromEpsgCode(crsName);
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON - try as plain string
            return DetectFromEpsgCode(crsObject);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromShapefilePrjAsync(string shapefilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shapefilePath))
            return null;

        // Replace .shp extension with .prj
        var prjPath = Path.ChangeExtension(shapefilePath, ".prj");

        if (!File.Exists(prjPath))
        {
            // Also check for .PRJ (uppercase)
            prjPath = Path.ChangeExtension(shapefilePath, ".PRJ");
            if (!File.Exists(prjPath))
                return null;
        }

        try
        {
            var prjContent = await File.ReadAllTextAsync(prjPath, cancellationToken);
            return await DetectFromPrjAsync(prjContent, cancellationToken);
        }
        catch (IOException)
        {
            // File exists but can't read it
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ValidateSridAsync(int srid, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            using var command = new NpgsqlCommand(
                "SELECT 1 FROM spatial_ref_sys WHERE srid = @srid LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@srid", srid);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result != null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Intentionally broad: fail closed and reject unvalidated SRIDs to prevent data
            // corruption. PostGIS would reject invalid SRIDs later anyway, but by then the
            // geometry data may already be stored with the wrong SRID.
            CrsLog.SridValidationFailed(_logger, ex, srid);
            return false;
        }
    }

    /// <summary>
    /// Attempt to find SRID by matching WKT against PostGIS spatial_ref_sys table.
    /// </summary>
    private async Task<int?> FindSridByWktMatchAsync(string wktContent, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            // Fail closed on WKT matching. Compare normalized WKT text only; do not use
            // prefix/substring heuristics that can map one projected CRS to another.
            using var command = new NpgsqlCommand(@"
                SELECT srid
                FROM spatial_ref_sys
                WHERE lower(regexp_replace(srtext, '\s+', '', 'g')) =
                      lower(regexp_replace(@wkt, '\s+', '', 'g'))
                LIMIT 1", connection);

            command.Parameters.AddWithValue("@wkt", wktContent);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as int?;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Intentionally generic: any failure querying spatial_ref_sys (connection, timeout,
            // malformed WKT) should fall through to "no match" rather than propagate, so the
            // import can continue down the remaining SRID-detection fallbacks.
            CrsLog.SridDetectionByWktFailed(_logger, ex);
            return null;
        }
    }

    private Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken);

    private static partial class CrsLog
    {
        // Warning, not Debug: this catch only fires on infrastructure failures (an unknown
        // SRID returns a null scalar, not an exception). At Debug a transient DB outage made
        // every import fail with a misleading "SRID is not registered" error while the real
        // cause was invisible at production log levels.
        [LoggerMessage(
            EventId = 7430,
            Level = LogLevel.Warning,
            Message = "SRID validation failed for srid={Srid}, rejecting to prevent data corruption")]
        public static partial void SridValidationFailed(ILogger logger, Exception exception, int srid);

        [LoggerMessage(
            EventId = 7431,
            Level = LogLevel.Debug,
            Message = "SRID detection by WKT match failed, returning null")]
        public static partial void SridDetectionByWktFailed(ILogger logger, Exception exception);
    }
}
