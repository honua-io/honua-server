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
    /// Root directory under which file-sink executors may create output files.
    /// Caller-supplied sink paths are always resolved relative to this directory;
    /// absolute paths and traversal outside the root are rejected.
    /// </summary>
    // Second segment is a fixed relative literal, so it can never be
    // rooted and silently discard Path.GetTempPath().
    public string OutputRootDirectory { get; set; } =
        Path.Join(Path.GetTempPath(), "honua-geoprocessing-outputs");

    /// <summary>
    /// Root directory that the <c>import.dataset</c> executor accepts as the staging area
    /// for source files. The caller-supplied <c>sourcePath</c> job input must resolve to a
    /// canonical path that starts with this prefix; paths outside the staging root are
    /// rejected with a job failure to prevent path-traversal attacks (BH3-027).
    /// Defaults to the same staging directory used by the upload pipeline.
    /// </summary>
    // Second segment is a fixed relative literal, so it can never be
    // rooted and silently discard Path.GetTempPath().
    public string ImportStagingDirectory { get; set; } =
        Path.Join(Path.GetTempPath(), "honua-import-staging");

    /// <summary>
    /// Retention TTL applied to durable geoprocessing result packages produced
    /// by built-in executors. Mirrors the default Redis store retention so
    /// configuration here is authoritative for both reads and writes.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00", ErrorMessage = "ResultRetention must be between 1 minute and 30 days")]
    public TimeSpan ResultRetention { get; set; } = TimeSpan.FromDays(7);
}
