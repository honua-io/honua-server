// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Ogc.Common;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.OgcFeatures.Services;

/// <summary>
/// Resolves request-side CRS headers for OGC Features write operations.
/// </summary>
internal static class OgcRequestCrsResolver
{
    internal static async Task<(bool IsValid, CrsDefinition Definition, string? Error)> TryResolveInputCrsAsync(
        HttpRequest request,
        LayerDefinition layer,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        var contentCrs = request.Headers.TryGetValue("Content-Crs", out var values)
            ? values.ToString()
            : null;
        var layerSrid = layer.SpatialReference.Srid;
        CrsDefinition definition;
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsDefinitionsAsync(
                layer,
                crsRegistry,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(contentCrs))
        {
            var defaultCrs = layerSrid == 4326
                ? OgcFeaturesUtilities.Crs84Uri
                : layerSrid.ToOgcCrs();

            if (!OgcFeaturesUtilities.TryResolveCrs(defaultCrs, supportedCrs, out definition, out var defaultError))
            {
                return (false, default, defaultError ?? $"Unsupported CRS '{defaultCrs}'.");
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

        if (definition.Srid != layerSrid)
        {
            return (false, default, $"Content-Crs SRID {definition.Srid} does not match layer SRID {layerSrid}.");
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
