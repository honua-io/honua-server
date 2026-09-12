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

    /// <summary>Cancellation deadline for layer execution; checked between admitted managed topology calls.</summary>
    [Range(1, 3600)]
    public int MaxLayerExecutionSeconds { get; set; } = 300;

    /// <summary>Maximum cumulative vertices materialized from each layer or in buffered intermediates.</summary>
    [Range(1, 1_000_000)]
    public int MaxLayerVertices { get; set; } = 100_000;

    /// <summary>
    /// Maximum conservative segment-pair work admitted to non-preemptible managed topology.
    /// Unary topology charges the square of all input vertices; joins charge both sides' product.
    /// This is an admission ceiling, not a claim that every admitted input takes equal time.
    /// </summary>
    [Range(1, 1_000_000_000)]
    public long MaxTopologyWork { get; set; } = 4_000_000;

    /// <summary>
    /// Maximum decoded byte length accepted for a deliberately inline raster source.
    /// Larger sources must use PostGIS, object-store, Zarr, or staged-artifact references.
    /// The upper bound remains small so neither the web heap nor the durable job store
    /// becomes part of the raster data plane.
    /// </summary>
    [Range(1024, 64 * 1024, ErrorMessage = "MaxInlineRasterSourceBytes must be between 1 KiB and 64 KiB")]
    public int MaxInlineRasterSourceBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum typed raster bindings accepted across one plan.</summary>
    [Range(1, 32, ErrorMessage = "MaxRasterSourcesPerPlan must be between 1 and 32")]
    public int MaxRasterSourcesPerPlan { get; set; } = 32;

    /// <summary>Maximum length of a typed raster process-parameter binding name.</summary>
    [Range(1, 64, ErrorMessage = "MaxRasterSourceParameterNameLength must be between 1 and 64")]
    public int MaxRasterSourceParameterNameLength { get; set; } = 64;

    /// <summary>Maximum serialized typed raster descriptor bytes across one plan.</summary>
    [Range(4096, 256 * 1024, ErrorMessage = "MaxRasterSourceSerializedBytesPerPlan must be between 4 KiB and 256 KiB")]
    public int MaxRasterSourceSerializedBytesPerPlan { get; set; } = 256 * 1024;

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

    /// <summary>
    /// Maximum feature count that the <c>sink.honua-layer</c> executor will materialize into
    /// a single transactional catalog load. A spilled <c>honua-feature-stream</c> input is
    /// read incrementally, but is still loaded through one transactional batch (server#4628),
    /// so this is the explicit enforced bound that keeps that buffering finite regardless of
    /// upstream dataset size.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxSinkFeatureCount must be a positive integer")]
    public int MaxSinkFeatureCount { get; set; } = 2_000_000;
}
