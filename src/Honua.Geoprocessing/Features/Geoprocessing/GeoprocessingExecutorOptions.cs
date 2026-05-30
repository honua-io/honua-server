// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing;

/// <summary>
/// Configuration guardrails applied by built-in production geoprocessing
/// executors. The defaults match the existing 7-day Redis result-package TTL
/// and a conservative 50 MB ceiling on per-job artifact payloads.
/// </summary>
internal sealed class GeoprocessingExecutorOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Geoprocessing:Executors";

    /// <summary>
    /// Maximum size, in bytes, of a single artifact payload that a built-in
    /// executor will publish. Executors must fail the job rather than truncate
    /// or persist a payload larger than this ceiling. The default is 50 MiB.
    /// </summary>
    [Range(1024, 1024L * 1024L * 1024L, ErrorMessage = "MaxArtifactBytes must be between 1 KiB and 1 GiB")]
    public long MaxArtifactBytes { get; set; } = 50L * 1024L * 1024L;

    /// <summary>
    /// Retention TTL applied to durable geoprocessing result packages produced
    /// by built-in executors. Mirrors the default Redis store retention so
    /// configuration here is authoritative for both reads and writes.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00", ErrorMessage = "ResultRetention must be between 1 minute and 30 days")]
    public TimeSpan ResultRetention { get; set; } = TimeSpan.FromDays(7);
}
