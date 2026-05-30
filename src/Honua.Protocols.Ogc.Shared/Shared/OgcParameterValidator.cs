// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Common;

#pragma warning disable CA1716 // The Shared namespace is intentional — mirrors the audit-fixes kernel placement (#1144).
namespace Honua.Server.Features.Protocols.Ogc.Shared;
#pragma warning restore CA1716

/// <summary>
/// 2D/3D bounding box parsed from an OGC KVP value.
/// </summary>
/// <remarks>
/// <para>For 2D inputs the Z components are <c>null</c>. The 3D form follows the
/// OGC API Common ordering of <c>minX,minY,minZ,maxX,maxY,maxZ</c>.</para>
/// <para>When a CRS suffix is supplied as a fifth or seventh comma-separated
/// token (e.g. <c>"10,10,20,20,EPSG:4326"</c>) it is exposed verbatim via
/// <see cref="CrsToken"/>; callers can then feed it through
/// <see cref="OgcParameterValidator.TryParseCrs"/>.</para>
/// </remarks>
internal readonly record struct OgcBbox(
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    double? MinZ,
    double? MaxZ,
    string? CrsToken)
{
    public bool Is3D => MinZ.HasValue && MaxZ.HasValue;
}

/// <summary>
/// Shared, protocol-neutral kernel for parsing OGC KVP request parameters.
/// </summary>
/// <remarks>
/// <para>All methods are pure (no I/O, no logging). They produce concise error
/// strings suitable for embedding either in an OWS <c>ExceptionReport</c>
/// (WMS/WFS/WMTS/WCS) <c>ExceptionText</c> element or in the <c>detail</c>
/// field of a problem+json (OGC API) response.</para>
/// <para>This validator unifies behaviour previously duplicated across
/// <c>WmsRequestHandlers</c>, <c>Wcs20Handler</c>, <c>WmtsRequestHandlers</c>,
/// and the OGC API endpoints. Existing low-level helpers
/// (<see cref="SpatialReferenceHelpers"/>, <see cref="RasterParsingHelpers"/>,
/// <see cref="OgcTemporalFilterParser"/>) remain the canonical building blocks
/// and are not duplicated here.</para>
/// </remarks>
internal static class OgcParameterValidator
{
    private const int MaxBboxLength = 256;
    private const int MaxLayersLength = 4096;

    /// <summary>
    /// Parses an OGC KVP bounding-box string. Supports the canonical 4-token
    /// (2D) and 6-token (3D) forms, and tolerates an optional trailing CRS
    /// identifier (5- or 7-token forms used by WMS/WFS 1.1.0+).
    /// </summary>
    public static bool TryParseBbox(string? input, out OgcBbox bbox, out string error)
    {
        bbox = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "BBOX must be provided as a comma-separated list of coordinates.";
            return false;
        }

        if (input.Length > MaxBboxLength)
        {
            error = "BBOX value is too long.";
            return false;
        }

        var parts = input.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (4 or 5 or 6 or 7))
        {
            error = "BBOX must contain 4 (2D) or 6 (3D) comma-separated numeric values, optionally followed by a CRS identifier.";
            return false;
        }

        int numericCount = parts.Length switch
        {
            4 or 5 => 4,
            6 or 7 => 6,
            _ => 0,
        };

        var coords = new double[numericCount];
        for (var i = 0; i < numericCount; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out coords[i]) ||
                !double.IsFinite(coords[i]))
            {
                error = "BBOX coordinates must be finite numeric values.";
                return false;
            }
        }

        string? crsToken = null;
        if (parts.Length == numericCount + 1)
        {
            crsToken = parts[numericCount];
            if (string.IsNullOrWhiteSpace(crsToken))
            {
                error = "BBOX CRS suffix must not be empty.";
                return false;
            }
        }

        if (numericCount == 4)
        {
            var minX = coords[0];
            var minY = coords[1];
            var maxX = coords[2];
            var maxY = coords[3];

            if (minX > maxX || minY > maxY)
            {
                error = "BBOX values must be ordered as minx,miny,maxx,maxy.";
                return false;
            }

            bbox = new OgcBbox(minX, minY, maxX, maxY, null, null, crsToken);
            return true;
        }
        else
        {
            var minX = coords[0];
            var minY = coords[1];
            var minZ = coords[2];
            var maxX = coords[3];
            var maxY = coords[4];
            var maxZ = coords[5];

            if (minX > maxX || minY > maxY || minZ > maxZ)
            {
                error = "BBOX values must be ordered as minx,miny,minz,maxx,maxy,maxz.";
                return false;
            }

            bbox = new OgcBbox(minX, minY, maxX, maxY, minZ, maxZ, crsToken);
            return true;
        }
    }

    /// <summary>
    /// Parses a CRS identifier (EPSG code, OGC URI, OGC URN, or CRS84).
    /// On success <paramref name="crsUri"/> is the canonical OGC URI form, so
    /// callers can compare CRS values without re-implementing normalisation.
    /// </summary>
    public static bool TryParseCrs(string? input, out string crsUri, out string error)
    {
        crsUri = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "CRS must be provided.";
            return false;
        }

        if (!SpatialReferenceHelpers.TryParseCrsDefinition(input, out var definition))
        {
            error = $"Unsupported CRS '{input}'.";
            return false;
        }

        crsUri = definition.Uri;
        return true;
    }

    /// <summary>
    /// Parses a VERSION (or ACCEPTVERSIONS member) value and confirms it is in
    /// the supplied set of supported versions. Comparison is case-insensitive
    /// to match OWS Common rules.
    /// </summary>
    public static bool TryParseVersion(
        string? input,
        IReadOnlyList<string> supported,
        out string version,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(supported);

        version = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "VERSION must be provided.";
            return false;
        }

        var trimmed = input.Trim();
        for (var i = 0; i < supported.Count; i++)
        {
            if (string.Equals(supported[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                version = supported[i];
                return true;
            }
        }

        error = $"Unsupported version '{trimmed}'. Supported versions: {string.Join(", ", supported)}.";
        return false;
    }

    /// <summary>
    /// Parses a comma-separated LAYERS / COVERAGES / COVERAGEID parameter into
    /// an ordered, de-duplicated list. Empty tokens are rejected.
    /// </summary>
    public static bool TryParseLayers(
        string? input,
        out IReadOnlyList<string> layers,
        out string error)
    {
        layers = Array.Empty<string>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "LAYERS must contain at least one identifier.";
            return false;
        }

        if (input.Length > MaxLayersLength)
        {
            error = "LAYERS value is too long.";
            return false;
        }

        var parts = input.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "LAYERS must contain at least one identifier.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                error = "LAYERS must not contain empty values.";
                return false;
            }

            if (seen.Add(part))
            {
                ordered.Add(part);
            }
        }

        layers = ordered;
        return true;
    }

    /// <summary>
    /// Parses an RFC 3339 datetime instant or interval (closed or open-ended).
    /// Wraps <see cref="OgcTemporalFilterParser.TryParseRange"/> and exposes
    /// non-nullable start / nullable end semantics for protocols that need an
    /// explicit instant when no interval is supplied.
    /// </summary>
    public static bool TryParseTime(
        string? input,
        out DateTimeOffset start,
        out DateTimeOffset? end,
        out string error)
    {
        start = default;
        end = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "TIME must be provided.";
            return false;
        }

        if (!OgcTemporalFilterParser.TryParseRange(input, out var parsedStart, out var parsedEnd, out var inner))
        {
            error = inner ?? "Invalid datetime parameter.";
            return false;
        }

        if (parsedStart is null && parsedEnd is null)
        {
            error = "TIME must be provided.";
            return false;
        }

        if (parsedStart is null)
        {
            // Open-start interval (../end) — protocol-specific handlers can
            // decide whether to allow this. Surface end as the start and leave
            // end null so the caller can detect the open-start case.
            start = parsedEnd!.Value;
            end = null;
            return true;
        }

        start = parsedStart.Value;
        end = parsedEnd;
        return true;
    }

    /// <summary>
    /// Parses a FORMAT (or media-type-like) parameter and confirms it is in
    /// the supplied list of supported values. Comparison is case-insensitive.
    /// </summary>
    public static bool TryParseFormat(
        string? input,
        IReadOnlyList<string> supported,
        out string format,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(supported);

        format = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "FORMAT must be provided.";
            return false;
        }

        var trimmed = input.Trim();
        for (var i = 0; i < supported.Count; i++)
        {
            if (string.Equals(supported[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                format = supported[i];
                return true;
            }
        }

        error = $"Unsupported format '{trimmed}'. Supported formats: {string.Join(", ", supported)}.";
        return false;
    }
}
