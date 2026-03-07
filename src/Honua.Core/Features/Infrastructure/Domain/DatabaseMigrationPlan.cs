// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Point-in-time migration plan for a database connection.
/// </summary>
public sealed record DatabaseMigrationPlan
{
    /// <summary>
    /// Whether the migration plan was generated successfully.
    /// </summary>
    public required bool Successful { get; init; }

    /// <summary>
    /// Whether one or more migration scripts are pending.
    /// </summary>
    public required bool UpgradeRequired { get; init; }

    /// <summary>
    /// Scripts that would be executed if migrations were applied now.
    /// </summary>
    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Scripts recorded as executed but no longer discovered by the configured script source.
    /// </summary>
    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Error message when plan generation fails.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Exception raised while building the plan, if any.
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// Creates a successful migration plan.
    /// </summary>
    public static DatabaseMigrationPlan Succeeded(
        IReadOnlyList<string>? pendingScripts = null,
        IReadOnlyList<string>? executedButNotDiscoveredScripts = null)
    {
        var pending = pendingScripts ?? Array.Empty<string>();

        return new DatabaseMigrationPlan
        {
            Successful = true,
            UpgradeRequired = pending.Count > 0,
            PendingScripts = pending,
            ExecutedButNotDiscoveredScripts = executedButNotDiscoveredScripts ?? Array.Empty<string>()
        };
    }

    /// <summary>
    /// Creates a failed migration plan.
    /// </summary>
    public static DatabaseMigrationPlan Failed(Exception error, string? errorMessage = null)
        => new()
        {
            Successful = false,
            UpgradeRequired = false,
            Error = error,
            ErrorMessage = errorMessage ?? error.Message
        };
}
