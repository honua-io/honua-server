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
            Error = error
        };
}
