// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Result of a database migration run.
/// </summary>
public sealed record DatabaseMigrationResult
{
    /// <summary>
    /// Whether the migration completed successfully.
    /// </summary>
    public required bool Successful { get; init; }

    /// <summary>
    /// Names of migration scripts that were applied.
    /// </summary>
    public IReadOnlyList<string> AppliedScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error message when a migration fails.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Exception raised during migration, if any.
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// Creates a successful migration result.
    /// </summary>
    /// <param name="appliedScripts">Scripts applied during the migration run.</param>
    public static DatabaseMigrationResult Succeeded(IReadOnlyList<string>? appliedScripts = null)
        => new()
        {
            Successful = true,
            AppliedScripts = appliedScripts ?? Array.Empty<string>()
        };

    /// <summary>
    /// Creates a failed migration result.
    /// </summary>
    /// <param name="error">The exception that caused the failure.</param>
    /// <param name="errorMessage">Optional error message override.</param>
    /// <param name="appliedScripts">Scripts applied before the failure.</param>
    public static DatabaseMigrationResult Failed(
        Exception error,
        string? errorMessage = null,
        IReadOnlyList<string>? appliedScripts = null)
        => new()
        {
            Successful = false,
            Error = error,
            ErrorMessage = errorMessage ?? error.Message,
            AppliedScripts = appliedScripts ?? Array.Empty<string>()
        };
}
