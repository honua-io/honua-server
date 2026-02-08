// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for detecting coordinate reference systems from various sources
/// Behavior reference: ../Honua.Server/src/platform/core/Query/Filter/CqlFilterParser.cs
/// Implements CRS detection from .prj files, WKT, EPSG codes, and GeoJSON
/// </summary>
internal sealed partial class CrsDetectionService : ICrsDetectionService
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<CrsDetectionService> _logger;

    /// <summary>
    /// Common EPSG codes and their variations for quick lookup
    /// </summary>
    private static readonly FrozenDictionary<string, int> _wellKnownEpsgCodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // WGS84 variants (including ArcGIS-style names)
            ["WGS84"] = 4326,
            ["WGS_84"] = 4326,
            ["WGS 84"] = 4326,
            ["WGS_1984"] = 4326,
            ["GCS_WGS_1984"] = 4326,
            ["D_WGS_1984"] = 4326,
            ["EPSG:4326"] = 4326,
            ["4326"] = 4326,

            // Web Mercator variants
            ["EPSG:3857"] = 3857,
            ["3857"] = 3857,
            ["GOOGLE_MERCATOR"] = 3857,
            ["WEB_MERCATOR"] = 3857,
            ["PSEUDO_MERCATOR"] = 3857,

            // NAD83 variants (including ArcGIS-style names)
            ["NAD83"] = 4269,
            ["NAD_83"] = 4269,
            ["NAD_1983"] = 4269,
            ["GCS_North_American_1983"] = 4269,
            ["D_North_American_1983"] = 4269,
            ["EPSG:4269"] = 4269,
            ["4269"] = 4269,
        }
        .ToFrozenDictionary();

    /// <summary>
    /// Regex patterns for extracting EPSG codes from various string formats
    /// </summary>
    private static readonly Regex _epsgCodeRegex = new(
        @"(?:EPSG:|AUTHORITY\[""EPSG"",""?)(\d{3,6})(?:""|])?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public CrsDetectionService(IDatabaseConnectionProvider connectionProvider, ILogger<CrsDetectionService> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int?> DetectFromPrjAsync(string prjContent)
    {
        if (string.IsNullOrWhiteSpace(prjContent))
            return null;

        // Clean up the content
        var cleanedContent = prjContent.Trim();

        // Try EPSG code extraction first (most reliable)
        var epsgMatch = _epsgCodeRegex.Match(cleanedContent);
        if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var epsgCode))
        {
            if (await ValidateSridAsync(epsgCode))
                return epsgCode;
        }

        // Check for well-known coordinate system names, but only when the WKT
        // is not a projected CRS. Many projected CRSs (UTM zones, State Plane, etc.)
        // contain "WGS 84" or "NAD83" in their datum name, which would cause false matches.
        var isProjected = cleanedContent.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) ||
                          cleanedContent.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase);

        if (!isProjected)
        {
            foreach (var kvp in _wellKnownEpsgCodes)
            {
                if (cleanedContent.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    if (await ValidateSridAsync(kvp.Value))
                        return kvp.Value;
                }
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
        var epsgMatch = _epsgCodeRegex.Match(cleanedContent);
        if (epsgMatch.Success && int.TryParse(epsgMatch.Groups[1].Value, out var epsgCode))
        {
            if (await ValidateSridAsync(epsgCode))
                return epsgCode;
        }

        // Check for well-known coordinate system names, but only when the WKT
        // is not a projected CRS to avoid false matches on datum names
        var isProjected = cleanedContent.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) ||
                          cleanedContent.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase);

        if (!isProjected)
        {
            foreach (var kvp in _wellKnownEpsgCodes)
            {
                if (cleanedContent.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    if (await ValidateSridAsync(kvp.Value))
                        return kvp.Value;
                }
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
            // Validate reasonable EPSG/SRID range (includes user-defined SRIDs up to 999999)
            if (srid > 0 && srid <= 999999)
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
            await using var connection = await OpenConnectionAsync();

            using var command = new NpgsqlCommand(
                "SELECT 1 FROM spatial_ref_sys WHERE srid = @srid LIMIT 1",
                connection);
            command.Parameters.AddWithValue("@srid", srid);

            var result = await command.ExecuteScalarAsync();
            return result != null;
        }
        catch (Exception ex)
        {
            // Fail closed: reject unvalidated SRIDs to prevent data corruption.
            // PostGIS would reject invalid SRIDs later anyway, but by then the
            // geometry data may already be stored with the wrong SRID.
            CrsLog.SridValidationFailed(_logger, ex, srid);
            return false;
        }
    }

    /// <summary>
    /// Attempt to find SRID by matching WKT against PostGIS spatial_ref_sys table
    /// </summary>
    private async Task<int?> FindSridByWktMatchAsync(string wktContent)
    {
        try
        {
            await using var connection = await OpenConnectionAsync();

            // Try to find exact or similar WKT match
            // Note: This is a simplified approach - full WKT comparison is complex
            using var command = new NpgsqlCommand(@"
                SELECT srid
                FROM spatial_ref_sys
                WHERE srtext = @wkt OR srtext ILIKE @pattern
                ORDER BY CASE
                    WHEN srtext = @wkt THEN 1
                    ELSE 2
                END
                LIMIT 1", connection);

            // Create a simplified search pattern
            var pattern = $"%{wktContent.Substring(0, Math.Min(50, wktContent.Length))}%";
            command.Parameters.AddWithValue("@wkt", wktContent);
            command.Parameters.AddWithValue("@pattern", pattern);

            var result = await command.ExecuteScalarAsync();
            return result as int?;
        }
        catch (Exception ex)
        {
            // If we can't query PostGIS, return null
            CrsLog.SridDetectionByWktFailed(_logger, ex);
            return null;
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = await _connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
        if (connection is NpgsqlConnection npgsqlConnection)
        {
            return npgsqlConnection;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException("Expected NpgsqlConnection for CRS detection.");
    }

    private static partial class CrsLog
    {
        [LoggerMessage(
            EventId = 7430,
            Level = LogLevel.Debug,
            Message = "SRID validation failed for srid={Srid}, rejecting to prevent data corruption")]
        public static partial void SridValidationFailed(ILogger logger, Exception exception, int srid);

        [LoggerMessage(
            EventId = 7431,
            Level = LogLevel.Debug,
            Message = "SRID detection by WKT match failed, returning null")]
        public static partial void SridDetectionByWktFailed(ILogger logger, Exception exception);
    }
}
