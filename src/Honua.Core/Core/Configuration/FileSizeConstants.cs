namespace Honua.Core.Configuration;

public static class FileSizeConstants
{
    public const long OneKB = 1024;

    public const long OneMB = 1024 * OneKB;

    public const long OneGB = 1024 * OneMB;

    public const long DefaultMaxFileSize = 100 * OneMB;

    public const long SmallFileLimit = 10 * OneMB;

    public const long LargeFileLimit = OneGB;

    public const long MaxRasterFileSize = 500 * OneMB;

    // The synchronous PostGIS import path materializes one byte array, so a larger raster must use the durable staged-import pipeline.
    public const long MaxDirectRasterImportSize = FiftyMB;

    public const long MaxVectorFileSize = 200 * OneMB;

    public const long TenMB = 10 * OneMB;

    public const long FiftyMB = 50 * OneMB;

    public const long OneHundredMB = 100 * OneMB;

    public const long FiveHundredMB = 500 * OneMB;
}
