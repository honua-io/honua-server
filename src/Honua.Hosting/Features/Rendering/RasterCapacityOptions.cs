// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Configures fail-fast admission for synchronous raster serving. Limits apply per
/// serving instance; durable GP workers have their own execution admission.
/// </summary>
public sealed class RasterCapacityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RasterCapacity";

    /// <summary>Maximum cells in a synchronous web raster response.</summary>
    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxWebOutputCells { get; set; } = 16_777_216;

    /// <summary>Maximum estimated managed bytes used to materialize a synchronous web response.</summary>
    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxWebOutputBytes { get; set; } = 64L * 1024L * 1024L;

    /// <summary>Maximum object-store range requests addressed by one synchronous request.</summary>
    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxObjectRangeRequests { get; set; } = 512;

    /// <summary>Maximum conservative aggregate object-store range bytes per synchronous request.</summary>
    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxObjectRangeBytes { get; set; } = 256L * 1024L * 1024L;

    /// <summary>
    /// Maximum provider-estimated PostGIS raster work units per synchronous request.
    /// PostGIS adapters define units from output cells, bands, sources, and transform cost.
    /// </summary>
    [Range(typeof(long), "1", "9223372036854775807")]
    public long MaxPostGisWorkUnits { get; set; } = 67_108_864;

    /// <summary>Maximum active synchronous raster requests on one serving instance.</summary>
    [Range(1, int.MaxValue)]
    public int MaxConcurrentRequests { get; set; } = 8;

    /// <summary>Maximum active synchronous raster requests in one tenant fairness partition.</summary>
    [Range(1, int.MaxValue)]
    public int MaxConcurrentRequestsPerTenant { get; set; } = 2;

    /// <summary>Retry-After value emitted for transient concurrency denials.</summary>
    [Range(1, 3600)]
    public int RetryAfterSeconds { get; set; } = 1;
}
