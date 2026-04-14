using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Limits for file preview and import operations.
/// </summary>
public class ImportLimits
{
    /// <summary>
    /// Maximum file size that can be previewed.
    /// </summary>
    [Range(1_048_576, 52_428_800)]
    public long MaxPreviewSize { get; set; } = FileSizeConstants.TenMB;

    /// <summary>
    /// Maximum file size allowed for synchronous imports.
    /// </summary>
    [Range(10_485_760, 524_288_000)]
    public long MaxSyncImportSize { get; set; } = FileSizeConstants.FiftyMB;

    /// <summary>
    /// Maximum file size allowed for any import.
    /// </summary>
    [Range(52_428_800, 5_368_709_120L)]
    public long MaxImportSize { get; set; } = FileSizeConstants.FiveHundredMB;

    /// <summary>
    /// Maximum number of features returned in a preview.
    /// </summary>
    [Range(10, 1000)]
    public int MaxPreviewFeatures { get; set; } = 100;

    /// <summary>
    /// Maximum number of rows scanned while deriving preview counts.
    /// </summary>
    [Range(10, 1_000_000)]
    public int MaxPreviewCountScan { get; set; } = 100_000;

    /// <summary>
    /// Batch size for import writes.
    /// </summary>
    [Range(100, 10_000)]
    public int BatchSize { get; set; } = 1_000;
}

/// <summary>
/// Limits for spatial analytics operations.
/// </summary>
public class AnalyticsLimits
{
    /// <summary>
    /// Maximum number of input features processed by analytics queries.
    /// </summary>
    [Range(100, 1_000_000)]
    public int MaxInputFeatures { get; set; } = 100_000;

    /// <summary>
    /// Maximum cluster count returned by clustering queries.
    /// </summary>
    [Range(10, 100_000)]
    public int MaxClusters { get; set; } = 10_000;

    /// <summary>
    /// Maximum DBSCAN epsilon distance in meters.
    /// </summary>
    [Range(1, 1_000_000)]
    public double MaxDbscanEpsMeters { get; set; } = 100_000;

    /// <summary>
    /// Maximum K value for K-means.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxKMeansK { get; set; } = 1_000;

    /// <summary>
    /// Maximum buffer distance in meters.
    /// </summary>
    [Range(1, 1_000_000)]
    public double MaxBufferDistanceMeters { get; set; } = 100_000;

    /// <summary>
    /// Minimum density cell size in meters.
    /// </summary>
    [Range(1, 1_000_000)]
    public double MinDensityCellSizeMeters { get; set; } = 10;

    /// <summary>
    /// Maximum density cell size in meters.
    /// </summary>
    [Range(1, 1_000_000)]
    public double MaxDensityCellSizeMeters { get; set; } = 100_000;

    /// <summary>
    /// Maximum density cell count returned by density queries.
    /// </summary>
    [Range(10, 1_000_000)]
    public int MaxDensityCells { get; set; } = 10_000;

    /// <summary>
    /// Maximum dwithin distance in meters.
    /// </summary>
    [Range(1, 1_000_000)]
    public double MaxDWithinDistanceMeters { get; set; } = 100_000;
}
