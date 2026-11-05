// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing;

internal sealed class GeoprocessingExecutorOptions
{
    public const string SectionName = "Geoprocessing:Executors";

    // An artifact over this ceiling fails the job and is never truncated.
    [Range(1024, 1024L * 1024L * 1024L, ErrorMessage = "MaxArtifactBytes must be between 1 KiB and 1 GiB")]
    public long MaxArtifactBytes { get; set; } = 50L * 1024L * 1024L;

    [Range(1, 3600)]
    public int MaxLayerExecutionSeconds { get; set; } = 300;

    [Range(1, 1_000_000)]
    public int MaxLayerVertices { get; set; } = 100_000;

    [Range(1, 1_000_000_000)]
    public long MaxTopologyWork { get; set; } = 4_000_000;

    [Range(1024, 64 * 1024, ErrorMessage = "MaxInlineRasterSourceBytes must be between 1 KiB and 64 KiB")]
    public int MaxInlineRasterSourceBytes { get; set; } = 64 * 1024;

    [Range(1, 32, ErrorMessage = "MaxRasterSourcesPerPlan must be between 1 and 32")]
    public int MaxRasterSourcesPerPlan { get; set; } = 32;

    [Range(1, 64, ErrorMessage = "MaxRasterSourceParameterNameLength must be between 1 and 64")]
    public int MaxRasterSourceParameterNameLength { get; set; } = 64;

    [Range(4096, 256 * 1024, ErrorMessage = "MaxRasterSourceSerializedBytesPerPlan must be between 4 KiB and 256 KiB")]
    public int MaxRasterSourceSerializedBytesPerPlan { get; set; } = 256 * 1024;

    // Second segment is a fixed relative literal, so it can never be
    // rooted and silently discard Path.GetTempPath().
    public string OutputRootDirectory { get; set; } =
        Path.Join(Path.GetTempPath(), "honua-geoprocessing-outputs");

    // sourcePath must resolve under this root so a job cannot traverse outside the staging directory.
    // Second segment is a fixed relative literal, so it can never be
    // rooted and silently discard Path.GetTempPath().
    public string ImportStagingDirectory { get; set; } =
        Path.Join(Path.GetTempPath(), "honua-import-staging");

    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00", ErrorMessage = "ResultRetention must be between 1 minute and 30 days")]
    public TimeSpan ResultRetention { get; set; } = TimeSpan.FromDays(7);

    [Range(1, int.MaxValue, ErrorMessage = "MaxSinkFeatureCount must be a positive integer")]
    public int MaxSinkFeatureCount { get; set; } = 2_000_000;
}
