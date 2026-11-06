using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public class ImportSizeLimits
{
    [Range(1_048_576, 52_428_800)]
    public long MaxPreviewSize { get; set; } = FileSizeConstants.TenMB;

    [Range(10_485_760, 524_288_000)]
    public long MaxSyncImportSize { get; set; } = FileSizeConstants.FiftyMB;

    [Range(52_428_800, 5_368_709_120L)]
    public long MaxImportSize { get; set; } = FileSizeConstants.FiveHundredMB;

    [Range(52_428_800, 5_368_709_120L)]
    public long MaxTilePackageSize { get; set; } = 2L * FileSizeConstants.OneGB;

    [Range(10, 1000)]
    public int MaxPreviewFeatures { get; set; } = 100;

    [Range(10, 1_000_000)]
    public int MaxPreviewCountScan { get; set; } = 100_000;

    [Range(100, 10_000)]
    public int BatchSize { get; set; } = 1_000;
}

public class AnalyticsLimits
{
    // The floor is 1 so a deployment can set a low cap and force large analytics jobs onto the async path.
    [Range(1, 1_000_000)]
    public int MaxInputFeatures { get; set; } = 100_000;

    // The byte cap is charged while streaming, independent of feature count, so a few very large features cannot exhaust worker memory.
    [Range(1, long.MaxValue)]
    public long MaxInputBytes { get; set; } = 512L * 1024 * 1024;

    [Range(10, 100_000)]
    public int MaxClusters { get; set; } = 10_000;

    [Range(1, 1_000_000)]
    public double MaxDbscanEpsMeters { get; set; } = 100_000;

    [Range(1, 10_000)]
    public int MaxKMeansK { get; set; } = 1_000;

    [Range(1, 1_000_000)]
    public double MaxBufferDistanceMeters { get; set; } = 100_000;

    [Range(1, 1_000_000)]
    public double MinDensityCellSizeMeters { get; set; } = 10;

    [Range(1, 1_000_000)]
    public double MaxDensityCellSizeMeters { get; set; } = 100_000;

    [Range(10, 1_000_000)]
    public int MaxDensityCells { get; set; } = 10_000;

    [Range(1, 1_000_000)]
    public double MaxDWithinDistanceMeters { get; set; } = 100_000;
}
