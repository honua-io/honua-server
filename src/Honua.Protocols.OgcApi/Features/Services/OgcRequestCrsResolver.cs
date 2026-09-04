// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Resolves request-side CRS headers for OGC Features write operations.
/// </summary>
internal static class OgcRequestCrsResolver
{
    /// <summary>
    /// Resolves the request body CRS, defaulting to CRS84 when Content-Crs is absent
    /// as required for GeoJSON request bodies, regardless of the storage CRS.
    /// </summary>
    internal static async Task<(bool IsValid, CrsDefinition Definition, string? Error)> TryResolveInputCrsAsync(
        HttpRequest request,
        MetadataV2Resource resource,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(crsRegistry);

        var contentCrs = request.Headers.TryGetValue("Content-Crs", out var values)
            ? values.ToString()
            : null;
        CrsDefinition definition;
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsDefinitionsAsync(
                resource,
                crsRegistry,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(contentCrs))
        {
            // OGC API Features Part 4 uses the Part 1 default CRS for a body when
            // Content-Crs is absent. GeoJSON's default is CRS84 regardless of the
            // collection storage CRS; the geometry writer will transform from CRS84
            // into storage below. Treating a projected storage CRS as the input CRS
            // interprets lon/lat GeoJSON coordinates as metres and corrupts writes.
            if (!OgcFeaturesUtilities.TryResolveCrs(
                    OgcFeaturesUtilities.Crs84Uri,
                    supportedCrs,
                    out definition,
                    out var defaultError))
            {
                return (false, default, defaultError ?? $"Unsupported CRS '{OgcFeaturesUtilities.Crs84Uri}'.");
            }
        }
        else
        {
            var normalized = NormalizeContentCrs(contentCrs);
            if (!OgcFeaturesUtilities.TryResolveCrs(normalized, supportedCrs, out definition, out var resolveError))
            {
                return (false, default, resolveError ?? $"Unsupported CRS '{contentCrs}'.");
            }
        }

        return (true, definition, null);
    }

    private static string NormalizeContentCrs(string contentCrs)
    {
        var trimmed = contentCrs.Trim();
        var semicolonIndex = trimmed.IndexOf(';');
        if (semicolonIndex >= 0)
        {
            trimmed = trimmed[..semicolonIndex];
        }

        trimmed = trimmed.Trim();
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>') && trimmed.Length > 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return OgcFeaturesUtilities.NormalizeCrsUri(trimmed);
    }
}
