// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Configuration guardrails for the heavyweight GDAL worker executors.
/// </summary>
internal sealed class GdalWorkerOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "GdalWorker";

    /// <summary>
    /// Maximum size, in bytes, of a single source payload accepted from the
    /// durable spec, and of the produced artifact published back. Mirrors the
    /// lean executor 50 MiB default so the two profiles share one ceiling.
    /// </summary>
    [Range(1024, 1024L * 1024L * 1024L, ErrorMessage = "MaxArtifactBytes must be between 1 KiB and 1 GiB")]
    public long MaxArtifactBytes { get; set; } = 50L * 1024L * 1024L;

    /// <summary>
    /// Root directory under which per-job scratch workspaces are created. Each
    /// job gets an isolated subdirectory that is deleted after execution.
    /// Defaults to the OS temp directory.
    /// </summary>
    public string ScratchRoot { get; set; } = Path.Combine(Path.GetTempPath(), "honua-gdal-worker");

    /// <summary>
    /// Maximum wall-clock duration for a single GDAL tool invocation before the
    /// child process is killed and the job fails.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "06:00:00", ErrorMessage = "ToolTimeout must be between 5 seconds and 6 hours")]
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(15);
}
