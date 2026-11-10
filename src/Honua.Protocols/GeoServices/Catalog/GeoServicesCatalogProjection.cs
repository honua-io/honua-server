// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;

namespace Honua.Protocols.GeoServices.Catalog;

public sealed record GeoServicesCatalogEntry(string Name, string Type, string Url, string? DisplayName = null);

public static class GeoServicesCatalogProjection
{
    // The projection reads only FeatureServer and MapServer from the caller-supplied scope and never expands that snapshot or bypasses directory access checks.
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
            new GeoServicesCatalogEntry(entry.LogicalName ?? entry.Name, entry.Type, entry.Url, entry.DisplayName)).ToArray();
    }
}
