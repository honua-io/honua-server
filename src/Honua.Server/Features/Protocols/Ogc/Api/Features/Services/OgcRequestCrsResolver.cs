// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

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

        return (true, definition, null);
    }

    /// <summary>
    /// Metadata v2 overload of
    /// <see cref="TryResolveInputCrsAsync(HttpRequest, LayerDefinition, ICrsRegistry, CancellationToken)"/>.
    /// Reads the storage SRID from the V2 resource via
    /// <see cref="MetadataV2SpatialExtensions.ReadSrid"/>, defaulting to CRS84 when the
    /// resource declares no SRID.
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
        var resourceSrid = resource.ReadSrid();
        CrsDefinition definition;
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsDefinitionsAsync(
                resource,
                crsRegistry,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(contentCrs))
        {
            var defaultCrs = !resourceSrid.HasValue || resourceSrid.Value == 4326
                ? OgcFeaturesUtilities.Crs84Uri
                : resourceSrid.Value.ToOgcCrs();

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
