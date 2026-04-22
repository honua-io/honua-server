// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Focused service responsible only for file format detection.
/// Extracted from the original StreamingFileImportService god class.
/// Follows Single Responsibility Principle by handling only format detection logic.
/// </summary>
internal sealed class FileFormatDetectionService : IFileFormatDetectionService
{
    private readonly ILogger<FileFormatDetectionService> _logger;

    /// <summary>
    /// Supported file extensions mapped to formats
    /// </summary>
    private static readonly FrozenDictionary<string, SupportedFileFormat> FileExtensions =
        new Dictionary<string, SupportedFileFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".geojson"] = SupportedFileFormat.GeoJson,
            [".json"] = SupportedFileFormat.GeoJson,
            [".kml"] = SupportedFileFormat.Kml,
            [".kmz"] = SupportedFileFormat.Kml,
            [".wkt"] = SupportedFileFormat.Wkt,
            [".zip"] = SupportedFileFormat.Shapefile,
            [".gpkg"] = SupportedFileFormat.GeoPackage,
            [".gpx"] = SupportedFileFormat.Gpx,
            [".csv"] = SupportedFileFormat.Csv,
            [".gdb"] = SupportedFileFormat.FileGdb,
            [".parquet"] = SupportedFileFormat.GeoParquet,
            [".geoparquet"] = SupportedFileFormat.GeoParquet,
            [".fgb"] = SupportedFileFormat.FlatGeobuf
        }
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<ReadOnlyMemory<byte>, SupportedFileFormat> MagicNumbers =
        new Dictionary<ReadOnlyMemory<byte>, SupportedFileFormat>
        {
            [Encoding.UTF8.GetBytes("PK").AsMemory()] = SupportedFileFormat.Shapefile, // ZIP files
            [Encoding.UTF8.GetBytes("SQLite format 3").AsMemory()] = SupportedFileFormat.GeoPackage,
            [new byte[] { 0x66, 0x67, 0x62, 0x03 }.AsMemory()] = SupportedFileFormat.FlatGeobuf, // FGB magic
            [Encoding.UTF8.GetBytes("PAR1").AsMemory()] = SupportedFileFormat.GeoParquet, // Parquet magic
        }
        .ToFrozenDictionary();

    public FileFormatDetectionService(ILogger<FileFormatDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detect file format from filename extension
    /// </summary>
    public SupportedFileFormat? DetectFormat(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Handle special case for .gdb.zip files
        if (fileName.EndsWith(".gdb.zip", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedFileFormat.FileGdb;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        var detected = FileExtensions.GetValueOrDefault(extension);
        if (detected != default)
        {
            FileFormatDetectionLog.DetectedFromExtension(_logger, detected, extension, fileName);
        }

        return detected;
    }

    /// <summary>
    /// Get supported file extensions
    /// </summary>
    public string[] GetSupportedExtensions()
    {
        return FileExtensions.Keys.Append(".gdb.zip").ToArray();
    }

    /// <summary>
    /// Detect file format from content analysis using magic numbers
    /// </summary>
    public SupportedFileFormat? DetectFormatFromContent(ReadOnlySpan<byte> fileContent, string fileName)
    {
        if (fileContent.Length < 4)
        {
            return DetectFormat(fileName); // Fall back to filename detection
        }

        // Check magic numbers for binary formats
        foreach (var (magicBytes, format) in MagicNumbers)
        {
            if (fileContent.StartsWith(magicBytes.Span))
            {
                FileFormatDetectionLog.DetectedFromMagicNumber(_logger, format, fileName);
                return format;
            }
        }

        // Check for text-based formats
        var textContent = TryDecodeAsText(fileContent);
        if (!string.IsNullOrEmpty(textContent))
        {
            var detectedFormat = DetectTextFormat(textContent, fileName);
            if (detectedFormat.HasValue)
            {
                FileFormatDetectionLog.DetectedFromContentAnalysis(_logger, detectedFormat.Value, fileName);
                return detectedFormat.Value;
            }
        }

        // Fall back to filename-based detection
        return DetectFormat(fileName);
    }

    /// <summary>
    /// Attempt to decode binary content as text
    /// </summary>
    private static string? TryDecodeAsText(ReadOnlySpan<byte> content)
    {
        try
        {
            // Try UTF-8 first
            return Encoding.UTF8.GetString(content);
        }
        catch
        {
            try
            {
                // Fall back to ASCII
                return Encoding.ASCII.GetString(content);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Detect format from text content
    /// </summary>
    private static SupportedFileFormat? DetectTextFormat(string textContent, string fileName)
    {
        var trimmed = textContent.TrimStart();

        // GeoJSON detection
        if (trimmed.StartsWith('{') &&
            (trimmed.Contains("\"type\"") &&
             (trimmed.Contains("\"FeatureCollection\"") ||
              trimmed.Contains("\"Feature\"") ||
              trimmed.Contains("\"Point\"") ||
              trimmed.Contains("\"LineString\"") ||
              trimmed.Contains("\"Polygon\""))))
        {
            return SupportedFileFormat.GeoJson;
        }

        // KML detection
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<kml"))
        {
            return SupportedFileFormat.Kml;
        }

        // GPX detection
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<gpx"))
        {
            return SupportedFileFormat.Gpx;
        }

        // WKT detection (simple heuristic)
        if (trimmed.StartsWith("POINT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("LINESTRING", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("MULTIPOINT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("MULTILINESTRING", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("MULTIPOLYGON", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("GEOMETRYCOLLECTION", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedFileFormat.Wkt;
        }

        // CSV detection (basic heuristic - contains commas and appears tabular)
        if (trimmed.Contains(',') && trimmed.Contains('\n'))
        {
            var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                var firstLineCommas = lines[0].Count(c => c == ',');
                var secondLineCommas = lines[1].Count(c => c == ',');
                if (firstLineCommas > 0 && firstLineCommas == secondLineCommas)
                {
                    return SupportedFileFormat.Csv;
                }
            }
        }

        return null;
    }
}
