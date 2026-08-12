// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Canonical authenticated content route for staged geoprocessing output artifacts
/// (#3089). Every protocol adapter links staged outputs through this one route so
/// re-authorization, read leasing, and streaming live in a single handler; durable
/// records and result links never carry provider URLs or credentials.
/// </summary>
public static class RasterOutputContentRoutes
{
    /// <summary>Route pattern registered by the OGC API Processes jobs surface.</summary>
    public const string RoutePattern = "/ogc/processes/jobs/{jobId}/results/artifacts/{artifactIndex:int}/content";

    /// <summary>Builds the absolute content link for one staged artifact.</summary>
    /// <param name="baseUrl">Request base URL without a trailing slash.</param>
    /// <param name="jobId">Durable job identifier.</param>
    /// <param name="artifactIndex">Zero-based artifact position within the result package.</param>
    /// <returns>The absolute content URL.</returns>
    public static string Build(string baseUrl, string jobId, int artifactIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactIndex);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUrl.TrimEnd('/')}/ogc/processes/jobs/{Uri.EscapeDataString(jobId)}/results/artifacts/{artifactIndex}/content");
    }
}
