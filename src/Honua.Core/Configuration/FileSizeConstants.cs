namespace Honua.Core.Configuration;

/// <summary>
/// Standard file size constants for consistent limits across the system.
/// </summary>
public static class FileSizeConstants
{
    /// <summary>
    /// 1 KB in bytes.
    /// </summary>
    public const long OneKB = 1024;

    /// <summary>
    /// 1 MB in bytes.
    /// </summary>
    public const long OneMB = 1024 * OneKB;

    /// <summary>
    /// 1 GB in bytes.
    /// </summary>
    public const long OneGB = 1024 * OneMB;

    /// <summary>
    /// Default maximum file size (100 MB).
    /// </summary>
    public const long DefaultMaxFileSize = 100 * OneMB;

    /// <summary>
    /// Small file size limit (10 MB).
    /// </summary>
    public const long SmallFileLimit = 10 * OneMB;

    /// <summary>
    /// Large file size limit (1 GB).
    /// </summary>
    public const long LargeFileLimit = OneGB;

    /// <summary>
    /// Maximum raster file size (500 MB).
    /// </summary>
    public const long MaxRasterFileSize = 500 * OneMB;

    /// <summary>
    /// Maximum vector file size (200 MB).
    /// </summary>
    public const long MaxVectorFileSize = 200 * OneMB;

    /// <summary>
    /// 10 MB in bytes.
    /// </summary>
    public const long TenMB = 10 * OneMB;

    /// <summary>
    /// 50 MB in bytes.
    /// </summary>
    public const long FiftyMB = 50 * OneMB;

    /// <summary>
    /// 100 MB in bytes.
    /// </summary>
    public const long OneHundredMB = 100 * OneMB;

    /// <summary>
    /// 500 MB in bytes.
    /// </summary>
    public const long FiveHundredMB = 500 * OneMB;
}
