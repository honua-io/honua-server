// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.FeatureServer.Models;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Shared service for resolving spatial reference system identifiers from various input formats.
/// </summary>
internal class SpatialReferenceResolver
{
    private readonly ICrsDetectionService _crsDetectionService;
    private readonly ICrsRegistry _crsRegistry;

    public SpatialReferenceResolver(
        ICrsDetectionService crsDetectionService,
        ICrsRegistry crsRegistry)
    {
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
    }

    /// <summary>
    /// Resolves spatial reference identifier from string value or geometry spatial reference.
    /// </summary>
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

    private async Task<int?> EnsureSupportedAsync(int? srid, CancellationToken cancellationToken)
    {
        if (!srid.HasValue || srid.Value <= 0)
        {
            return null;
        }

        return await _crsRegistry.IsSridSupportedAsync(srid.Value, cancellationToken)
            .ConfigureAwait(false)
            ? srid
            : null;
    }

    /// <summary>
    /// Parses SRID from various string formats.
    /// </summary>
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

        if (LooksLikeWkt(trimmed))
        {
            return await _crsDetectionService.DetectFromWktAsync(trimmed).ConfigureAwait(false);
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

        if (!string.IsNullOrWhiteSpace(spatialRef.Wkt))
        {
            return await _crsDetectionService.DetectFromWktAsync(spatialRef.Wkt).ConfigureAwait(false);
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

        if (!string.IsNullOrWhiteSpace(parseResult.Wkt))
        {
            return await _crsDetectionService.DetectFromWktAsync(parseResult.Wkt).ConfigureAwait(false);
        }

        return null;
    }

    private static SpatialReferenceJsonParseResult TryParseSpatialReferenceJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;

            int? wkid = null;
            string? wkt = null;
            string? name = null;

            if (root.TryGetProperty("wkid", out var wkidElement) && wkidElement.TryGetInt32(out var wkidValue))
            {
                wkid = wkidValue;
            }

            if (!wkid.HasValue &&
                root.TryGetProperty("latestWkid", out var latestElement) &&
                latestElement.TryGetInt32(out var latestWkid))
            {
                wkid = latestWkid;
            }

            if (root.TryGetProperty("wkt", out var wktElement))
            {
                wkt = wktElement.GetString();
            }

            if (root.TryGetProperty("name", out var nameElement))
            {
                name = nameElement.GetString();
            }

            var hasValidData = wkid.HasValue || !string.IsNullOrWhiteSpace(wkt) || !string.IsNullOrWhiteSpace(name);
            return new SpatialReferenceJsonParseResult(hasValidData, wkid, wkt, name);
        }
        catch (JsonException)
        {
            return SpatialReferenceJsonParseResult.Failed;
        }
    }

    private static bool LooksLikeWkt(string value)
    {
        return value.StartsWith("GEOGCS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("PROJCS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("GEOGCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("PROJCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("GEODCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("GEODETICCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("COMPD_CS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("COMPOUNDCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("VERT_CS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("VERTCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("LOCAL_CS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("LOCALCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("BOUNDCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ENGCRS[", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ENGINEERINGCRS[", StringComparison.OrdinalIgnoreCase);
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
