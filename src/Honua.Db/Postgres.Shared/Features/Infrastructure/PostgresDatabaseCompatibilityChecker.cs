// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// Checks PostGIS and PostgreSQL compatibility by querying pg_extension and version().
/// Runs once at startup before DI scopes are available.
/// </summary>
internal sealed partial class PostgresDatabaseCompatibilityChecker : IDatabaseCompatibilityChecker
{
    internal const string MalformedConnectionStringMessage =
        "The database connection string could not be parsed (malformed-connection-string). "
        + "Quote any value that contains '=' or ';', for example a generated password that begins with '='.";

    private readonly ILogger<PostgresDatabaseCompatibilityChecker> _logger;

    public PostgresDatabaseCompatibilityChecker(ILogger<PostgresDatabaseCompatibilityChecker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DatabaseCompatibilityResult> CheckCompatibilityAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        // A connection string the driver cannot parse is a configuration fault, not a database fault.
        // The driver's message repeats the offending text, which includes the password, so only the
        // exception type is reported (honua-server#6495: a generated password that begins with '='
        // reads as "Password==..." and fails here).
        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            LogConnectionStringMalformed(_logger, ex.GetType().Name);
            return DatabaseCompatibilityResult.Incompatible(MalformedConnectionStringMessage);
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 1. Get engine version
            var engineVersion = await GetEngineVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            LogEngineVersion(_logger, engineVersion);

            // 2. Get installed extensions
            var extensions = await GetInstalledExtensionsAsync(connection, cancellationToken).ConfigureAwait(false);

            // A fresh database (for example the customer-install pgrouting image) ships the PostGIS
            // packages but has not run CREATE EXTENSION yet. The preflight runs before migrations,
            // so create it here when the role is allowed to (honua-server#6488). A role without the
            // privilege falls through to the incompatible-database refusal below.
            if (!extensions.Any(e => string.Equals(e.Name, "postgis", StringComparison.OrdinalIgnoreCase))
                && await TryCreatePostGisExtensionAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                extensions = await GetInstalledExtensionsAsync(connection, cancellationToken).ConfigureAwait(false);
            }

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
        // Intentionally generic: this is a startup compatibility probe; any failure must be reported
        // to the caller as a result rather than crashing startup. A transient availability failure
        // (connection-slot exhaustion, timeout, socket failure) says nothing about the PostGIS install
        // and is reported as Unavailable so startup can degrade instead of exiting; everything else
        // (permissions, unexpected catalog shape) stays Incompatible.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // honua-server#5916: name the cause class. Only incompatible-database is fatal; a
            // connection that could not be borrowed, a refused endpoint, rejected credentials or any
            // other availability failure degrades startup instead of reporting an incompatible database.
            var causeClass = PostgresPreflightFailureClassifier.Classify(ex);
            if (DatabasePreflightCauseClasses.IsDegradable(causeClass))
            {
                var failureClass = PostgresPreflightFailureClassifier.Describe(ex);
                LogCompatibilityCheckUnavailable(_logger, causeClass, failureClass, ex);
                return DatabaseCompatibilityResult.Unavailable(failureClass, ex, causeClass);
            }

            const string errorMessage = "Database compatibility check failed (incompatible-database).";
            LogCompatibilityCheckFailed(_logger, ex.Message, ex);
            return DatabaseCompatibilityResult.Incompatible(errorMessage, error: ex);
        }
    }

    private async Task<bool> TryCreatePostGisExtensionAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS postgis", connection);
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            LogPostGisCreated(_logger);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A concurrent startup can lose the CREATE race and still leave PostGIS installed.
            // Re-query before deciding. A disconnect or command timeout is not a missing
            // extension: it has to reach PostgresPreflightFailureClassifier.
            var installed = await PostGisIsInstalledAsync(connection, cancellationToken).ConfigureAwait(false);
            switch (DecidePostGisCreateFailure(ex, installed))
            {
                case PostGisCreateFailureDisposition.CreatedByPeer:
                    LogPostGisCreatedConcurrently(_logger);
                    return true;
                case PostGisCreateFailureDisposition.MissingExtension:
                    LogPostGisCreateFailed(_logger, ex.GetType().Name, ex.Message);
                    return false;
                default:
                    throw;
            }
        }
    }

    /// <summary>What a failed <c>CREATE EXTENSION postgis</c> means after a re-query.</summary>
    internal enum PostGisCreateFailureDisposition
    {
        /// <summary>The role cannot create the extension, or the packages are not installed.</summary>
        MissingExtension = 0,

        /// <summary>The failure is not a missing extension and must reach the preflight classifier.</summary>
        Propagate = 1,

        /// <summary>The re-query found PostGIS, so a concurrent create won the race.</summary>
        CreatedByPeer = 2,
    }

    /// <summary>
    /// Classifies a create failure after <paramref name="installedAfterFailure"/> was read from
    /// <c>pg_extension</c>. Privilege and missing-package errors stay a missing extension.
    /// Everything else propagates.
    /// </summary>
    /// <param name="error">The exception raised by <c>CREATE EXTENSION</c>.</param>
    /// <param name="installedAfterFailure">True when PostGIS is installed after that exception.</param>
    /// <returns>How the preflight should treat the failure.</returns>
    internal static PostGisCreateFailureDisposition DecidePostGisCreateFailure(
        Exception error, bool installedAfterFailure)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (installedAfterFailure)
        {
            return PostGisCreateFailureDisposition.CreatedByPeer;
        }

        return IsPostGisExtensionUnavailable(error)
            ? PostGisCreateFailureDisposition.MissingExtension
            : PostGisCreateFailureDisposition.Propagate;
    }

    private static bool IsPostGisExtensionUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                // 42501 insufficient_privilege, 58P01 undefined_file (no control file),
                // 42704 undefined_object, 0A000 feature_not_supported.
                return postgres.SqlState is "42501" or "0A000" or "58P01" or "42704";
            }
        }

        return false;
    }

    private static async Task<bool> PostGisIsInstalledAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var extensions = await GetInstalledExtensionsAsync(connection, cancellationToken).ConfigureAwait(false);
        return extensions.Exists(extension => string.Equals(extension.Name, "postgis", StringComparison.OrdinalIgnoreCase));
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
        EventId = 7917,
        Level = LogLevel.Information,
        Message = "PostGIS extension was not installed; created it with CREATE EXTENSION postgis.")]
    private static partial void LogPostGisCreated(ILogger logger);

    [LoggerMessage(
        EventId = 7918,
        Level = LogLevel.Warning,
        Message = "Could not create the PostGIS extension ({ExceptionType}): {Reason}. Run CREATE EXTENSION postgis as a privileged role.")]
    private static partial void LogPostGisCreateFailed(ILogger logger, string exceptionType, string reason);

    [LoggerMessage(
        EventId = 7921,
        Level = LogLevel.Information,
        Message = "PostGIS extension was created by a concurrent startup; the preflight will use it.")]
    private static partial void LogPostGisCreatedConcurrently(ILogger logger);

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

    [LoggerMessage(
        EventId = 7916,
        Level = LogLevel.Warning,
        Message = "Database compatibility check could not obtain a usable connection ({CauseClass}: {FailureClass}).")]
    private static partial void LogCompatibilityCheckUnavailable(ILogger logger, string causeClass, string failureClass, Exception ex);

    [LoggerMessage(
        EventId = 7919,
        Level = LogLevel.Error,
        Message = "The configured database connection string could not be parsed ({ExceptionType}). The value is not logged because it contains credentials.")]
    private static partial void LogConnectionStringMalformed(ILogger logger, string exceptionType);
}
