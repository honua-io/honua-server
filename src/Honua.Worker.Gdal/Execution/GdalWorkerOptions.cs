// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Configuration;

namespace Honua.Worker.Gdal.Execution;

internal sealed class GdalWorkerOptions
{
    public const string SectionName = "GdalWorker";

    [Range(1024, 1024L * 1024L * 1024L, ErrorMessage = "MaxArtifactBytes must be between 1 KiB and 1 GiB")]
    public long MaxArtifactBytes { get; set; } = 50L * 1024L * 1024L;

    [Range(1024L * 1024L, 1024L * 1024L * 1024L * 1024L,
        ErrorMessage = "MaxStagedArtifactBytes must be between 1 MiB and 1 TiB")]
    public long MaxStagedArtifactBytes { get; set; } = 10L * 1024L * 1024L * 1024L;

    [Range(1024L, FileSizeConstants.OneGB, ErrorMessage = "MaxArchiveEntryBytes must be between 1 KiB and 1 GiB")]
    public long MaxArchiveEntryBytes { get; set; } = FileSizeConstants.FiveHundredMB;

    [Range(1024L, 4L * FileSizeConstants.OneGB, ErrorMessage = "MaxArchiveExtractedBytes must be between 1 KiB and 4 GiB")]
    public long MaxArchiveExtractedBytes { get; set; } = FileSizeConstants.OneGB;

    [Range(2d, 1_000_000d, ErrorMessage = "MaxArchiveCompressionRatio must be between 2 and 1000000")]
    public double MaxArchiveCompressionRatio { get; set; } = 200d;

    [Range(1, 1_000_000, ErrorMessage = "MaxArchiveEntries must be between 1 and 1000000")]
    public int MaxArchiveEntries { get; set; } = 100_000;

    // Second segment is a fixed relative literal, so it can never be rooted and
    // silently discard Path.GetTempPath().
    public string ScratchRoot { get; set; } = Path.Join(Path.GetTempPath(), "honua-gdal-worker");

    [Range(typeof(TimeSpan), "00:00:05", "06:00:00", ErrorMessage = "ToolTimeout must be between 5 seconds and 6 hours")]
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(15);

    [Range(1, int.MaxValue, ErrorMessage = "MaxRasterWidth must be a positive pixel count")]
    public int MaxRasterWidth { get; set; } = 100_000;

    [Range(1, int.MaxValue, ErrorMessage = "MaxRasterHeight must be a positive pixel count")]
    public int MaxRasterHeight { get; set; } = 100_000;

    [Range(1L, long.MaxValue, ErrorMessage = "MaxRasterPixels must be a positive cell count")]
    public long MaxRasterPixels { get; set; } = 500_000_000L;

    [Range(1, int.MaxValue, ErrorMessage = "MaxRasterBands must be a positive band count")]
    public int MaxRasterBands { get; set; } = 512;

    [Range(1024L, long.MaxValue, ErrorMessage = "MaxDecodedRasterBytes must be at least 1 KiB")]
    public long MaxDecodedRasterBytes { get; set; } = 4L * 1024L * 1024L * 1024L;

    public IList<string> AllowedRasterInputFormats { get; set; } = new List<string>
    {
        "TIFF",
        "PNG",
        "JPEG",
    };

    [Range(1, int.MaxValue, ErrorMessage = "MaxZoneCount must be a positive feature count")]
    public int MaxZoneCount { get; set; } = 10_000;

    [Range(1L, long.MaxValue, ErrorMessage = "MaxZoneVertices must be a positive vertex count")]
    public long MaxZoneVertices { get; set; } = 2_000_000L;

    [Range(2, int.MaxValue, ErrorMessage = "MaxKrigingSamples must be at least 2 sample points")]
    public int MaxKrigingSamples { get; set; } = 1_024;

    [Range(1L, long.MaxValue, ErrorMessage = "MaxKrigingCells must be a positive cell count")]
    public long MaxKrigingCells { get; set; } = 4_000_000L;

    [Range(1L, long.MaxValue, ErrorMessage = "MaxKrigingPredictionWork must be a positive evaluation count")]
    public long MaxKrigingPredictionWork { get; set; } = 250_000_000L;
}
