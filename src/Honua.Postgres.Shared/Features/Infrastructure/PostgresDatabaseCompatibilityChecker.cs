// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Checks PostGIS and PostgreSQL compatibility by querying pg_extension and version().
/// Runs once at startup before DI scopes are available.
/// </summary>
internal sealed partial class PostgresDatabaseCompatibilityChecker : IDatabaseCompatibilityChecker
{
    private readonly ILogger<PostgresDatabaseCompatibilityChecker> _logger;

    public PostgresDatabaseCompatibilityChecker(ILogger<PostgresDatabaseCompatibilityChecker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DatabaseCompatibilityResult> CheckCompatibilityAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 1. Get engine version
            var engineVersion = await GetEngineVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            LogEngineVersion(_logger, engineVersion);

            // 2. Get installed extensions
            var extensions = await GetInstalledExtensionsAsync(connection, cancellationToken).ConfigureAwait(false);

            var hasPostGis = false;
            var hasPostGisRaster = false;
            string? postGisVersion = null;
            string? postGisRasterVersion = null;

            foreach (var (name, version) in extensions)
            {
                if (string.Equals(name, "postgis", StringComparison.OrdinalIgnoreCase))
                {
                    hasPostGis = true;
                    postGisVersion = version;
                }
                else if (string.Equals(name, "postgis_raster", StringComparison.OrdinalIgnoreCase))
                {
                    hasPostGisRaster = true;
                    postGisRasterVersion = version;
                }
            }

            var extensionNames = extensions.Select(e => e.Name).ToList();
            var warnings = new List<string>();

            // 3. Validate PostGIS presence
            if (!hasPostGis)
            {
                LogPostGisMissing(_logger);
                return DatabaseCompatibilityResult.Incompatible(
                    "PostGIS extension is not installed. Honua requires PostGIS for spatial operations.",
                    engineVersion,
                    extensionNames,
                    warnings);
            }

            LogPostGisVersion(_logger, postGisVersion!);

            // 4. Check PostGIS raster (warning, not failure)
            if (!hasPostGisRaster)
            {
                LogPostGisRasterMissing(_logger);
                warnings.Add("PostGIS raster extension is not installed. Raster features will be unavailable.");
            }
            else
            {
                LogPostGisRasterVersion(_logger, postGisRasterVersion!);
            }

            return DatabaseCompatibilityResult.Compatible(
                engineVersion,
                postGisVersion,
                postGisRasterVersion,
                extensionNames,
                warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errorMessage = $"Database compatibility check failed: {ex.Message}";
            LogCompatibilityCheckFailed(_logger, ex.Message, ex);
            return DatabaseCompatibilityResult.Incompatible(errorMessage, error: ex);
        }
    }

    private static async Task<string> GetEngineVersionAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT version()", connection);
        command.CommandTimeout = 10;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result?.ToString() ?? "unknown";
    }

    private static async Task<List<(string Name, string Version)>> GetInstalledExtensionsAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT extname, extversion FROM pg_extension ORDER BY extname", connection);
        command.CommandTimeout = 10;

        var extensions = new List<(string Name, string Version)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            extensions.Add((reader.GetString(0), reader.GetString(1)));
        }

        return extensions;
    }

    [LoggerMessage(
        EventId = 7910,
        Level = LogLevel.Information,
        Message = "Database engine: {EngineVersion}")]
    private static partial void LogEngineVersion(ILogger logger, string engineVersion);

    [LoggerMessage(
        EventId = 7911,
        Level = LogLevel.Information,
        Message = "PostGIS version: {PostGisVersion}")]
    private static partial void LogPostGisVersion(ILogger logger, string postGisVersion);

    [LoggerMessage(
        EventId = 7912,
        Level = LogLevel.Information,
        Message = "PostGIS raster: {RasterVersion}")]
    private static partial void LogPostGisRasterVersion(ILogger logger, string rasterVersion);

    [LoggerMessage(
        EventId = 7913,
        Level = LogLevel.Warning,
        Message = "PostGIS extension is not installed. Honua requires PostGIS for spatial operations.")]
    private static partial void LogPostGisMissing(ILogger logger);

    [LoggerMessage(
        EventId = 7914,
        Level = LogLevel.Warning,
        Message = "PostGIS raster extension is not installed. Raster features will be unavailable.")]
    private static partial void LogPostGisRasterMissing(ILogger logger);

    [LoggerMessage(
        EventId = 7915,
        Level = LogLevel.Error,
        Message = "Database compatibility check failed: {ErrorMessage}")]
    private static partial void LogCompatibilityCheckFailed(ILogger logger, string errorMessage, Exception ex);
}
