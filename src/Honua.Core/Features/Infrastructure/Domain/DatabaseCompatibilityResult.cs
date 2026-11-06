// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record DatabaseCompatibilityResult
{
    public required bool IsCompatible { get; init; }

    public required string EngineVersion { get; init; }

    public string? PostGisVersion { get; init; }

    public string? PostGisRasterVersion { get; init; }

    public required IReadOnlyList<string> InstalledExtensions { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public string? ErrorMessage { get; init; }

    public Exception? Error { get; init; }

    // A transient outage is not an incompatible database, so startup degrades instead of refusing the install.
    public bool IsTransientFailure { get; init; }

    // The failure class never includes connection-string fragments.
    public string? FailureClass { get; init; }

    // Only an incompatible database is fatal at startup; the other cause classes degrade.
    public string? CauseClass { get; init; }

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
