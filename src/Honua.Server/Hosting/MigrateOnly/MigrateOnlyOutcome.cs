// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Hosting.MigrateOnly;

// Exit code 1 is unused because the runtime also returns it for an unhandled exception.
internal sealed record MigrateOnlyOutcome
{
    private MigrateOnlyOutcome(int exitCode, string code, string outcome)
    {
        ExitCode = exitCode;
        Code = code;
        Outcome = outcome;
    }

    public int ExitCode { get; }

    public string Code { get; }

    public string Outcome { get; }

    public static MigrateOnlyOutcome Completed { get; } = new(0, "completed", "completed");

    public static MigrateOnlyOutcome InvalidConfiguration { get; } = new(2, "invalid-configuration", "refused");

    public static MigrateOnlyOutcome DatabaseUnavailable { get; } = new(3, "database-unavailable", "failed");

    public static MigrateOnlyOutcome DatabaseIncompatible { get; } = new(4, "database-incompatible", "refused");

    public static MigrateOnlyOutcome ContractApprovalRequired { get; } = new(5, "contract-approval-required", "refused");

    public static MigrateOnlyOutcome UnannotatedContractMigration { get; } = new(6, "unannotated-contract-migration", "refused");

    public static MigrateOnlyOutcome BackupFailed { get; } = new(7, "backup-failed", "refused");

    public static MigrateOnlyOutcome SchemaNewerThanBinary { get; } = new(8, "schema-newer-than-binary", "refused");

    // Another migrator held the advisory lock past the wait.
    public static MigrateOnlyOutcome MigrationLockTimeout { get; } = new(9, "migration-lock-timeout", "failed");

    public static MigrateOnlyOutcome MigrationFailed { get; } = new(10, "migration-failed", "failed");

    public static MigrateOnlyOutcome Cancelled { get; } = new(11, "cancelled", "failed");

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
