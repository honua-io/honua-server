// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

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
    /// Whether the migration journal already contains applied scripts. A non-empty journal means this
    /// plan targets an existing database rather than a fresh install.
    /// </summary>
    public bool JournalIsNonEmpty { get; init; }

    /// <summary>
    /// Expand/contract safety classification for each pending script, in the order the scripts
    /// would be applied. Populated by runners that can read script contents; empty otherwise.
    /// </summary>
    public IReadOnlyList<MigrationScriptClassification> PendingScriptClassifications { get; init; }
        = Array.Empty<MigrationScriptClassification>();

    /// <summary>
    /// Whether any pending script is a potentially backward-incompatible ("contract") change that
    /// lacks the compatibility-review marker. Such scripts are rejected by the runner (fail closed).
    /// </summary>
    public bool HasUnannotatedBreakingScripts =>
        PendingScriptClassifications.Any(
            c => c.Classification == MigrationSafetyClassification.ContractUnannotated);

    /// <summary>
    /// Whether any pending script is a potentially backward-incompatible ("contract") change,
    /// whether or not it is annotated. Contract migrations must run in the contract phase and must
    /// not ride along a rolling deploy (ADR-0060 expand/contract discipline).
    /// </summary>
    public bool HasContractScripts => PendingScriptClassifications.Any(c => c.IsBreaking);

    /// <summary>
    /// The names of pending contract-phase (breaking) scripts, whether or not annotated.
    /// </summary>
    public IReadOnlyList<string> ContractScriptNames =>
        PendingScriptClassifications.Where(c => c.IsBreaking).Select(c => c.ScriptName).ToArray();

    /// <summary>
    /// The names of pending contract-phase scripts that lack the compatibility-review marker.
    /// </summary>
    public IReadOnlyList<string> UnannotatedBreakingScriptNames =>
        PendingScriptClassifications
            .Where(c => c.Classification == MigrationSafetyClassification.ContractUnannotated)
            .Select(c => c.ScriptName)
            .ToArray();

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
        IReadOnlyList<string>? executedButNotDiscoveredScripts = null,
        IReadOnlyList<MigrationScriptClassification>? pendingScriptClassifications = null,
        bool journalIsNonEmpty = false)
    {
        var pending = pendingScripts ?? Array.Empty<string>();

        return new DatabaseMigrationPlan
        {
            Successful = true,
            UpgradeRequired = pending.Count > 0,
            PendingScripts = pending,
            ExecutedButNotDiscoveredScripts = executedButNotDiscoveredScripts ?? Array.Empty<string>(),
            JournalIsNonEmpty = journalIsNonEmpty,
            PendingScriptClassifications =
                pendingScriptClassifications ?? Array.Empty<MigrationScriptClassification>()
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
