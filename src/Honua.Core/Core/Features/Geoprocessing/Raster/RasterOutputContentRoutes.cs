// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Geoprocessing.Raster;

public static class RasterOutputContentRoutes
{
    public const string RoutePattern = "/api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex:int}/content";

    public static bool CanServe(
        Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? store,
        string? providerName,
        string? storeReference)
        => store is not null
           && string.Equals(store.Provider.ToString(), providerName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(store.StoreReference, storeReference, StringComparison.Ordinal);

    public static string Build(string baseUrl, string jobId, int artifactIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUrl.TrimEnd('/')}{BuildRelative(jobId, artifactIndex)}");
    }

    public static string BuildRelative(string jobId, int artifactIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactIndex);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"/api/geoprocessing/jobs/{Uri.EscapeDataString(jobId)}/artifacts/{artifactIndex}/content");
    }
}
