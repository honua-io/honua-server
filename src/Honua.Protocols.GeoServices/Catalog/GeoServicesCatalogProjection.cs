// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Protocols.GeoServices.Catalog;

/// <summary>A visible FeatureServer or MapServer entry from the canonical services directory.</summary>
/// <param name="Name">Canonical service name.</param>
/// <param name="Type">GeoServices service type.</param>
/// <param name="Url">Resolved public URL.</param>
public sealed record GeoServicesCatalogEntry(string Name, string Type, string Url);

/// <summary>Reuses the directory's routability and caller authorization for scoped catalog consumers.</summary>
public static class GeoServicesCatalogProjection
{
    /// <summary>
    /// Reads only FeatureServer and MapServer entries from a caller-supplied, scope-filtered snapshot.
    /// This never expands the snapshot or bypasses the directory's resource/service access checks.
    /// </summary>
    /// <param name="context">The original authenticated request context.</param>
    /// <param name="snapshot">The metadata snapshot restricted to the consumer's authorized scope.</param>
    /// <returns>The visible entries, preserving canonical ordering and duplicate suppression.</returns>
    public static async Task<IReadOnlyList<GeoServicesCatalogEntry>> ReadFeatureMapAsync(
        HttpContext context, MetadataV2GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        var services = context.RequestServices;
        var projection = await GeoservicesCatalogEndpoints.BuildServiceDirectoryProjectionAsync(
            context,
            services.GetRequiredService<IMetadataV2GraphProvider>(),
            services.GetRequiredService<IRasterStore>(),
            services.GetRequiredService<ILicenseStatusProvider>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GeoServicesCatalogProjection)),
            snapshot,
            featureMapOnly: true).ConfigureAwait(false);
        return projection.Entries.Select(static entry =>
            new GeoServicesCatalogEntry(entry.Name, entry.Type, entry.Url)).ToArray();
    }
}
