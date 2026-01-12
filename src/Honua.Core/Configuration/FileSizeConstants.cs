// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Centralized file size constants used throughout the application.
/// Prevents magic numbers and ensures consistency across validation, configuration, and documentation.
/// </summary>
public static class FileSizeConstants
{
    #region Base Byte Units

    /// <summary>
    /// 1 kilobyte in bytes (1,024 bytes).
    /// </summary>
    public const long OneKB = 1024;

    /// <summary>
    /// 1 megabyte in bytes (1,048,576 bytes).
    /// </summary>
    public const long OneMB = 1024 * 1024;

    /// <summary>
    /// 1 gigabyte in bytes (1,073,741,824 bytes).
    /// </summary>
    public const long OneGB = 1024 * 1024 * 1024;

    #endregion

    #region Common File Size Limits

    /// <summary>
    /// 100 kilobytes in bytes (102,400 bytes).
    /// Used for tile size limits and WKB validation.
    /// </summary>
    public const long OneHundredKB = 100 * OneKB;

    /// <summary>
    /// 500 kilobytes in bytes (512,000 bytes).
    /// Used for default tile size limits.
    /// </summary>
    public const long FiveHundredKB = 500 * OneKB;

    /// <summary>
    /// 5 megabytes in bytes (5,242,880 bytes).
    /// Used for maximum tile size limits.
    /// </summary>
    public const long FiveMB = 5 * OneMB;

    /// <summary>
    /// 10 megabytes in bytes (10,485,760 bytes).
    /// Used for geometry size limits, attachment limits, and preview limits.
    /// </summary>
    public const long TenMB = 10 * OneMB;

    /// <summary>
    /// 50 megabytes in bytes (52,428,800 bytes).
    /// Used for edit payload limits and import sync thresholds.
    /// </summary>
    public const long FiftyMB = 50 * OneMB;

    /// <summary>
    /// 100 megabytes in bytes (104,857,600 bytes).
    /// Used for maximum geometry sizes, attachment totals, and import memory limits.
    /// </summary>
    public const long OneHundredMB = 100 * OneMB;

    /// <summary>
    /// 500 megabytes in bytes (524,288,000 bytes).
    /// Used for maximum edit payload sizes and import file limits.
    /// </summary>
    public const long FiveHundredMB = 500 * OneMB;

    /// <summary>
    /// 5 gigabytes in bytes (5,368,709,120 bytes).
    /// Used for maximum import file size limits.
    /// </summary>
    public const long FiveGB = 5 * OneGB;

    #endregion

    #region Validation Helpers

    /// <summary>
    /// Gets a human-readable description of a file size in bytes.
    /// </summary>
    /// <param name="sizeInBytes">Size in bytes to format</param>
    /// <returns>Formatted string like "10 MB" or "1.5 GB"</returns>
    public static string FormatBytes(long sizeInBytes)
    {
        return sizeInBytes switch
        {
            < OneKB => $"{sizeInBytes} bytes",
            < OneMB => $"{sizeInBytes / OneKB} KB",
            < OneGB => $"{sizeInBytes / OneMB} MB",
            _ => $"{sizeInBytes / OneGB:F1} GB"
        };
    }

    /// <summary>
    /// Validates that a file size is within the specified range.
    /// </summary>
    /// <param name="sizeInBytes">Size to validate</param>
    /// <param name="minSize">Minimum allowed size</param>
    /// <param name="maxSize">Maximum allowed size</param>
    /// <returns>True if size is within range, false otherwise</returns>
    public static bool IsValidSize(long sizeInBytes, long minSize, long maxSize)
    {
        return sizeInBytes >= minSize && sizeInBytes <= maxSize;
    }

    #endregion
}
