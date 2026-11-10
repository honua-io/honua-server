// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record DatabaseMigrationPlan
{
    public required bool Successful { get; init; }

    public required bool UpgradeRequired { get; init; }

    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    public bool JournalIsNonEmpty { get; init; }

    public IReadOnlyList<DatabaseMigrationScriptBinding> ScriptBindings { get; init; }
        = Array.Empty<DatabaseMigrationScriptBinding>();

    public string? ContractApprovalNonce { get; init; }

    public IReadOnlyList<MigrationScriptClassification> PendingScriptClassifications { get; init; }
        = Array.Empty<MigrationScriptClassification>();

    public bool HasUnannotatedBreakingScripts =>
        PendingScriptClassifications.Any(
            c => c.Classification == MigrationSafetyClassification.ContractUnannotated);

    // A contract change must run in the contract phase and must not ride along a rolling deploy.
    public bool HasContractScripts => PendingScriptClassifications.Any(c => c.IsBreaking);

    public IReadOnlyList<string> ContractScriptNames =>
        PendingScriptClassifications.Where(c => c.IsBreaking).Select(c => c.ScriptName).ToArray();

    public IReadOnlyList<string> UnannotatedBreakingScriptNames =>
        PendingScriptClassifications
            .Where(c => c.Classification == MigrationSafetyClassification.ContractUnannotated)
            .Select(c => c.ScriptName)
            .ToArray();

    public string? ErrorMessage { get; init; }

    public Exception? Error { get; init; }

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

    public static DatabaseMigrationPlan Failed(Exception error, string? errorMessage = null)
        => new()
        {
            Successful = false,
            UpgradeRequired = false,
            Error = error,
            ErrorMessage = errorMessage ?? error.Message
        };
}
