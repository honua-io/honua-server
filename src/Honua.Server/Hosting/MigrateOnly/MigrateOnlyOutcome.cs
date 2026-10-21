// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Hosting.MigrateOnly;

/// <summary>
/// The typed result of a migrate-only run (honua-server#6470). Each refusal or failure has its own
/// process exit code and stable code string, so an orchestrator (a Kubernetes pre-upgrade Job, an
/// ECS <c>run-task</c>, a CI step) can branch on the reason without parsing logs. Exit code 1 is
/// deliberately unused: the .NET runtime also returns it for an unhandled exception.
/// </summary>
internal sealed record MigrateOnlyOutcome
{
    private MigrateOnlyOutcome(int exitCode, string code, string outcome)
    {
        ExitCode = exitCode;
        Code = code;
        Outcome = outcome;
    }

    /// <summary>Process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Stable, machine-readable reason.</summary>
    public string Code { get; }

    /// <summary><c>completed</c>, <c>refused</c> (a safety gate stopped the run) or <c>failed</c>.</summary>
    public string Outcome { get; }

    /// <summary>The journal is current: every pending script applied, or none was pending.</summary>
    public static MigrateOnlyOutcome Completed { get; } = new(0, "completed", "completed");

    /// <summary>The mode, provider, host or connection string is not usable. Nothing was attempted.</summary>
    public static MigrateOnlyOutcome InvalidConfiguration { get; } = new(2, "invalid-configuration", "refused");

    /// <summary>The database could not be reached (refused, timed out, out of connection slots).</summary>
    public static MigrateOnlyOutcome DatabaseUnavailable { get; } = new(3, "database-unavailable", "failed");

    /// <summary>The PostGIS preflight found an incompatible database (missing or unsupported PostGIS).</summary>
    public static MigrateOnlyOutcome DatabaseIncompatible { get; } = new(4, "database-incompatible", "refused");

    /// <summary>
    /// Annotated contract-phase scripts are pending on an existing database and
    /// <c>HONUA_APPROVE_CONTRACT_MIGRATIONS</c> does not carry their nonce. The summary prints it.
    /// </summary>
    public static MigrateOnlyOutcome ContractApprovalRequired { get; } = new(5, "contract-approval-required", "refused");

    /// <summary>A contract-phase script lacks the compatibility-review marker (<c>Enforce=true</c>).</summary>
    public static MigrateOnlyOutcome UnannotatedContractMigration { get; } = new(6, "unannotated-contract-migration", "refused");

    /// <summary>The pre-migration backup command failed, timed out or could not start.</summary>
    public static MigrateOnlyOutcome BackupFailed { get; } = new(7, "backup-failed", "refused");

    /// <summary>The journal records migrations this binary does not know: the schema is newer than the binary.</summary>
    public static MigrateOnlyOutcome SchemaNewerThanBinary { get; } = new(8, "schema-newer-than-binary", "refused");

    /// <summary>Another migrator held the migration advisory lock past the wait timeout.</summary>
    public static MigrateOnlyOutcome MigrationLockTimeout { get; } = new(9, "migration-lock-timeout", "failed");

    /// <summary>A script failed, or the journal and the physical schema disagree.</summary>
    public static MigrateOnlyOutcome MigrationFailed { get; } = new(10, "migration-failed", "failed");

    /// <summary>The process was asked to stop before the run finished.</summary>
    public static MigrateOnlyOutcome Cancelled { get; } = new(11, "cancelled", "failed");

    /// <summary>Every outcome, for documentation and tests.</summary>
    public static IReadOnlyList<MigrateOnlyOutcome> All { get; } =
    [
        Completed,
        InvalidConfiguration,
        DatabaseUnavailable,
        DatabaseIncompatible,
        ContractApprovalRequired,
        UnannotatedContractMigration,
        BackupFailed,
        SchemaNewerThanBinary,
        MigrationLockTimeout,
        MigrationFailed,
        Cancelled,
    ];
}
