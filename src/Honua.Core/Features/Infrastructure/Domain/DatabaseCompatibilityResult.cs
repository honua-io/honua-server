// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Result of a database compatibility check at startup.
/// </summary>
public sealed record DatabaseCompatibilityResult
{
    /// <summary>
    /// Whether the database meets all compatibility requirements.
    /// </summary>
    public required bool IsCompatible { get; init; }

    /// <summary>
    /// Database engine version string (e.g. "PostgreSQL 16.2").
    /// </summary>
    public required string EngineVersion { get; init; }

    /// <summary>
    /// PostGIS extension version, if installed.
    /// </summary>
    public string? PostGisVersion { get; init; }

    /// <summary>
    /// PostGIS raster extension version, if installed.
    /// </summary>
    public string? PostGisRasterVersion { get; init; }

    /// <summary>
    /// Extensions installed in the database.
    /// </summary>
    public required IReadOnlyList<string> InstalledExtensions { get; init; }

    /// <summary>
    /// Non-fatal warnings detected during the compatibility check.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Error message when the check determines incompatibility.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Exception raised during the compatibility check, if any.
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// Whether the check could not reach a verdict because the database was transiently
    /// unavailable (connection-slot exhaustion, a timeout, a socket failure). Such a result is not
    /// compatible, but it says nothing about the database's PostGIS install: startup treats it as
    /// an outage (degraded start) rather than as an incompatible database.
    /// </summary>
    public bool IsTransientFailure { get; init; }

    /// <summary>
    /// Sanitized class of the failure for health and log surfaces (for example
    /// <c>SQLSTATE 53300 (class 53: insufficient resources)</c>). Never carries connection-string
    /// fragments. Set for <see cref="Unavailable"/> results.
    /// </summary>
    public string? FailureClass { get; init; }

    /// <summary>
    /// Stable cause class of a failed check (see <see cref="DatabasePreflightCauseClasses"/>):
    /// <c>connection-borrow-timeout</c>, <c>connection-refused</c>, <c>auth</c>,
    /// <c>connection-unavailable</c> or <c>incompatible-database</c>. Only
    /// <c>incompatible-database</c> is fatal at startup; the others degrade. Null when compatible.
    /// </summary>
    public string? CauseClass { get; init; }

    /// <summary>
    /// Creates a compatible result with detected versions.
    /// </summary>
    public static DatabaseCompatibilityResult Compatible(
        string engineVersion,
        string? postGisVersion,
        string? postGisRasterVersion,
        IReadOnlyList<string> installedExtensions,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            IsCompatible = true,
            EngineVersion = engineVersion,
            PostGisVersion = postGisVersion,
            PostGisRasterVersion = postGisRasterVersion,
            InstalledExtensions = installedExtensions,
            Warnings = warnings ?? Array.Empty<string>()
        };

    /// <summary>
    /// Creates an incompatible result with an error message.
    /// </summary>
    public static DatabaseCompatibilityResult Incompatible(
        string errorMessage,
        string? engineVersion = null,
        IReadOnlyList<string>? installedExtensions = null,
        IReadOnlyList<string>? warnings = null,
        Exception? error = null)
        => new()
        {
            IsCompatible = false,
            EngineVersion = engineVersion ?? "unknown",
            InstalledExtensions = installedExtensions ?? Array.Empty<string>(),
            Warnings = warnings ?? Array.Empty<string>(),
            ErrorMessage = errorMessage,
            Error = error,
            CauseClass = DatabasePreflightCauseClasses.IncompatibleDatabase
        };

    /// <summary>
    /// Creates a result for a check that could not reach the database because of a transient
    /// failure. The result is not compatible, and <see cref="IsTransientFailure"/> is set.
    /// </summary>
    /// <param name="failureClass">Sanitized failure class (no connection-string fragments).</param>
    /// <param name="error">The underlying exception.</param>
    /// <param name="causeClass">
    /// Degradable cause class (<see cref="DatabasePreflightCauseClasses"/>); defaults to
    /// <see cref="DatabasePreflightCauseClasses.ConnectionUnavailable"/>.
    /// </param>
    public static DatabaseCompatibilityResult Unavailable(
        string failureClass,
        Exception? error = null,
        string causeClass = DatabasePreflightCauseClasses.ConnectionUnavailable)
        => new()
        {
            IsCompatible = false,
            IsTransientFailure = true,
            FailureClass = failureClass,
            CauseClass = causeClass,
            EngineVersion = "unknown",
            InstalledExtensions = Array.Empty<string>(),
            Warnings = Array.Empty<string>(),
            ErrorMessage = "Database unavailable during the compatibility check (" + causeClass + "): " + failureClass + ".",
            Error = error
        };
}
