// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

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
            [".esrijson"] = SupportedFileFormat.EsriJson,
            [".wkb"] = SupportedFileFormat.Wkb,
            [".kml"] = SupportedFileFormat.Kml,
            [".kmz"] = SupportedFileFormat.Kml,
            [".gml"] = SupportedFileFormat.Gml,
            [".wkt"] = SupportedFileFormat.Wkt,
            [".zip"] = SupportedFileFormat.Shapefile,
            [".gpkg"] = SupportedFileFormat.GeoPackage,
            [".gpx"] = SupportedFileFormat.Gpx,
            [".csv"] = SupportedFileFormat.Csv,
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

        if (FileExtensions.TryGetValue(extension, out var detected))
        {
            FileFormatDetectionLog.DetectedFromExtension(_logger, detected, extension, fileName);
            return detected;
        }

        return null;
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

        // Well-Known Binary has no fixed magic string; it is identified structurally by a leading
        // byte-order flag (0x00 XDR / 0x01 NDR) followed by a valid geometry-type uint32 (also
        // EWKB with its SRID/Z/M flag bits). This runs before the text-decode branch because a WKB
        // payload's low bytes are not printable text (honua-server#2353).
        if (LooksLikeWkb(fileContent))
        {
            FileFormatDetectionLog.DetectedFromContentAnalysis(_logger, SupportedFileFormat.Wkb, fileName);
            return SupportedFileFormat.Wkb;
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

        // Esri JSON detection MUST run before GeoJSON: an Esri feature set is a JSON object
        // whose features carry an "attributes" object and an Esri geometry ("rings"/"paths"/
        // "points"/"x","y"), plus Esri markers such as "geometryType":"esriGeometry..." or
        // "spatialReference". These payloads must never fall through to the GeoJSON branch and
        // be silently misinterpreted (honua-server#2352).
        if (trimmed.StartsWith('{') && LooksLikeEsriJson(trimmed))
        {
            return SupportedFileFormat.EsriJson;
        }

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

        // GML detection — look for the GML namespace or a FeatureCollection root element
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith('<'))
        {
            if (trimmed.Contains("opengis.net/gml") ||
                trimmed.Contains("FeatureCollection") ||
                trimmed.Contains(":FeatureCollection"))
            {
                return SupportedFileFormat.Gml;
            }
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

    /// <summary>
    /// Structurally recognises a Well-Known Binary (WKB/EWKB) geometry payload from its leading
    /// bytes: a byte-order flag (<c>0x00</c> big-endian/XDR or <c>0x01</c> little-endian/NDR)
    /// followed by a geometry-type <c>uint32</c>. Standard WKB uses base types 1-7
    /// (Point..GeometryCollection); ISO WKB adds Z/M variants (e.g. 1001, 2001, 3001); PostGIS
    /// EWKB sets the high flag bits <c>0x80000000</c> (Z), <c>0x40000000</c> (M), and
    /// <c>0x20000000</c> (SRID). After stripping the EWKB flag bits and normalising the ISO
    /// thousands offset, the base type must fall in 1-7 for the payload to be WKB.
    /// </summary>
    private static bool LooksLikeWkb(ReadOnlySpan<byte> content)
    {
        if (content.Length < 5)
        {
            return false;
        }

        var byteOrder = content[0];
        if (byteOrder is not (0x00 or 0x01))
        {
            return false;
        }

        // Read the 4-byte geometry type honouring the declared byte order.
        uint geometryType = byteOrder == 0x01
            ? (uint)(content[1] | (content[2] << 8) | (content[3] << 16) | (content[4] << 24))
            : (uint)((content[1] << 24) | (content[2] << 16) | (content[3] << 8) | content[4]);

        // Strip the EWKB Z/M/SRID flag bits (top three bits), then fold the ISO thousands offset
        // (1000 = Z, 2000 = M, 3000 = ZM) so every representation reduces to a base type 1-7.
        var baseType = (geometryType & 0x1FFFFFFF) % 1000;
        return baseType is >= 1 and <= 7;
    }

    /// <summary>
    /// Heuristically decide whether a JSON document is an Esri feature set rather than GeoJSON.
    /// Esri payloads use <c>attributes</c> (not GeoJSON <c>properties</c>) and Esri geometry
    /// shapes (<c>x</c>/<c>y</c>, <c>points</c>, <c>paths</c>, <c>rings</c>), and never carry a
    /// GeoJSON <c>"type":"Feature"</c>/<c>"FeatureCollection"</c> discriminator.
    /// </summary>
    private static bool LooksLikeEsriJson(string trimmed)
    {
        // A GeoJSON document is unambiguously identified by its "type" discriminator; if present,
        // this is not Esri JSON.
        if (trimmed.Contains("\"type\"", StringComparison.Ordinal) &&
            (trimmed.Contains("\"FeatureCollection\"", StringComparison.Ordinal) ||
             trimmed.Contains("\"Feature\"", StringComparison.Ordinal)))
        {
            return false;
        }

        var hasEsriMarker =
            trimmed.Contains("esriGeometry", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"geometryType\"", StringComparison.Ordinal) ||
            trimmed.Contains("\"spatialReference\"", StringComparison.Ordinal);

        var hasEsriGeometry =
            trimmed.Contains("\"rings\"", StringComparison.Ordinal) ||
            trimmed.Contains("\"paths\"", StringComparison.Ordinal) ||
            trimmed.Contains("\"attributes\"", StringComparison.Ordinal);

        return hasEsriMarker && hasEsriGeometry;
    }
}
