// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for detecting coordinate reference systems from various sources
/// Behavior reference: ../Honua.Server/src/platform/core/Query/Filter/CqlFilterParser.cs
/// Implements CRS detection from .prj files, WKT, EPSG codes, and GeoJSON
/// </summary>
public sealed class CrsDetectionService : ICrsDetectionService
{
    private readonly string _connectionString;

    /// <summary>
    /// Common EPSG codes and their variations for quick lookup
    /// </summary>
    private static readonly Dictionary<string, int> WellKnownEpsgCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // WGS84 variants
        ["WGS84"] = 4326,
        ["WGS_84"] = 4326,
        ["WGS 84"] = 4326,
        ["EPSG:4326"] = 4326,
        ["4326"] = 4326,

        // Web Mercator variants
        ["EPSG:3857"] = 3857,
        ["3857"] = 3857,
        ["GOOGLE_MERCATOR"] = 3857,
        ["WEB_MERCATOR"] = 3857,
        ["PSEUDO_MERCATOR"] = 3857,

        // NAD83 variants
        ["NAD83"] = 4269,
        ["NAD_83"] = 4269,
        ["EPSG:4269"] = 4269,
        ["4269"] = 4269,

        // State Plane common zones
        ["EPSG:2154"] = 2154, // RGF93 / Lambert-93 (France)
        ["EPSG:25832"] = 25832, // ETRS89 / UTM zone 32N (Europe)
        ["EPSG:32633"] = 32633, // WGS 84 / UTM zone 33N
    };

    /// <summary>
    /// Regex patterns for extracting EPSG codes from various string formats
    /// </summary>
    private static readonly Regex EpsgCodeRegex = new(
        @"(?:EPSG:|AUTHORITY\[""EPSG"",""?)(\d{4,5})(?:""|])?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public CrsDetectionService(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromPrjAsync(string prjContent)
    {
        if (string.IsNullOrWhiteSpace(prjContent))
            return null;

        // Clean up the content
        var cleanedContent = prjContent.Trim();

        // Try EPSG code extraction first (most reliable)
        var epsgMatch = EpsgCodeRegex.Match(cleanedContent);
        if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var epsgCode))
        {
            if (await ValidateSridAsync(epsgCode))
                return epsgCode;
        }

        // Check for well-known coordinate system names
        foreach (var kvp in WellKnownEpsgCodes)
        {
            if (cleanedContent.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (await ValidateSridAsync(kvp.Value))
                    return kvp.Value;
            }
        }

        // Fall back to WKT parsing
        return await DetectFromWktAsync(cleanedContent);
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromWktAsync(string wktContent)
    {
        if (string.IsNullOrWhiteSpace(wktContent))
            return null;

        var cleanedContent = wktContent.Trim();

        // Try EPSG authority code extraction
        var epsgMatch = EpsgCodeRegex.Match(cleanedContent);
        if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var epsgCode))
        {
            if (await ValidateSridAsync(epsgCode))
                return epsgCode;
        }

        // Check for common coordinate system names in WKT
        foreach (var kvp in WellKnownEpsgCodes)
        {
            if (cleanedContent.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (await ValidateSridAsync(kvp.Value))
                    return kvp.Value;
            }
        }

        // Try to match against PostGIS spatial_ref_sys by WKT comparison
        return await FindSridByWktMatchAsync(cleanedContent);
    }

    /// <inheritdoc />
    public int? DetectFromEpsgCode(string epsgCode)
    {
        if (string.IsNullOrWhiteSpace(epsgCode))
            return null;

        // Remove common prefixes and clean up
        var cleaned = epsgCode.Trim()
            .Replace("EPSG:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("SRID=", "", StringComparison.OrdinalIgnoreCase);

        if (int.TryParse(cleaned, out var srid))
        {
            // Validate reasonable EPSG code range
            if (srid >= 1000 && srid <= 99999)
                return srid;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromGeoJsonCrsAsync(string crsObject)
    {
        if (string.IsNullOrWhiteSpace(crsObject))
            return null;

        try
        {
            using var document = JsonDocument.Parse(crsObject);
            var root = document.RootElement;

            // GeoJSON CRS format: {"type": "name", "properties": {"name": "EPSG:4326"}}
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() == "name" &&
                root.TryGetProperty("properties", out var propertiesElement) &&
                propertiesElement.TryGetProperty("name", out var nameElement))
            {
                var crsName = nameElement.GetString();
                if (!string.IsNullOrEmpty(crsName))
                {
                    return DetectFromEpsgCode(crsName);
                }
            }

            // Alternative format with direct EPSG reference
            if (root.TryGetProperty("name", out var directNameElement))
            {
                var crsName = directNameElement.GetString();
                if (!string.IsNullOrEmpty(crsName))
                {
                    return DetectFromEpsgCode(crsName);
                }
            }
        }
        catch (JsonException)
        {
            // Invalid JSON - try as plain string
            return DetectFromEpsgCode(crsObject);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromShapefilePrjAsync(string shapefilePath)
    {
        if (string.IsNullOrWhiteSpace(shapefilePath))
            return null;

        // Replace .shp extension with .prj
        var prjPath = Path.ChangeExtension(shapefilePath, ".prj");

        if (!File.Exists(prjPath))
        {
            // Also check for .PRJ (uppercase)
            prjPath = Path.ChangeExtension(shapefilePath, ".PRJ");
            if (!File.Exists(prjPath))
                return null;
        }

        try
        {
            var prjContent = await File.ReadAllTextAsync(prjPath);
            return await DetectFromPrjAsync(prjContent);
        }
        catch (IOException)
        {
            // File exists but can't read it
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ValidateSridAsync(int srid)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(
                "SELECT 1 FROM spatial_ref_sys WHERE srid = @srid LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@srid", srid);

            var result = await command.ExecuteScalarAsync();
            return result != null;
        }
        catch (Exception)
        {
            // If we can't validate, assume it's valid to avoid blocking imports
            // PostGIS will validate when used
            return true;
        }
    }

    /// <summary>
    /// Attempt to find SRID by matching WKT against PostGIS spatial_ref_sys table
    /// </summary>
    private async Task<int?> FindSridByWktMatchAsync(string wktContent)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Try to find exact or similar WKT match
            // Note: This is a simplified approach - full WKT comparison is complex
            using var command = new NpgsqlCommand(@"
                SELECT srid
                FROM spatial_ref_sys
                WHERE srtext ILIKE @pattern
                   OR auth_name = 'EPSG' AND auth_srid IN (4326, 3857, 4269)
                ORDER BY CASE
                    WHEN srtext = @wkt THEN 1
                    WHEN srtext ILIKE @pattern THEN 2
                    ELSE 3
                END
                LIMIT 1", connection);

            // Create a simplified search pattern
            var pattern = $"%{wktContent.Substring(0, Math.Min(50, wktContent.Length))}%";
            command.Parameters.AddWithValue("@wkt", wktContent);
            command.Parameters.AddWithValue("@pattern", pattern);

            var result = await command.ExecuteScalarAsync();
            return result as int?;
        }
        catch (Exception)
        {
            // If we can't query PostGIS, return null
            return null;
        }
    }
}
