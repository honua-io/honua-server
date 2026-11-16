// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Restore;

public static class PreMigrationRestoreCodes
{
    public const string BackupRequired = "restore-backup-required";

    public const string BackupUnverified = "restore-backup-unverified";

    public const string BackupChecksumMismatch = "restore-backup-checksum-mismatch";

    public const string BackupImageMismatch = "restore-backup-image-mismatch";

    public const string BackupSchemaAhead = "restore-backup-schema-ahead";

    public const string ScopeViolation = "restore-scope-violation";

    public const string ReadbackIncomplete = "restore-readback-incomplete";

    public const string RestoreIncomplete = "restore-incomplete";

    public const string ProviderFailed = "restore-provider-failed";

    public const string RetargetUnsupported = "restore-retarget-unsupported";

    public const string AuditFailed = "restore-audit-failed";

    public const string TransportUnavailable = "restore-transport-unavailable";

    public const string TargetUnknown = "restore-target-unknown";
}

public enum RestoreDecisionKind
{
    NotRequired = 0,

    RestoreBeforeStart = 1,

    Refuse = 2,
}

public sealed record AppliedSchemaMigration(int Number, string? Classification, int MinReaderSchema);

public sealed record VerifiedPreMigrationBackup(string BackupId, int SchemaVersion, string Engine);

public sealed record RestoreAssessment
{
    public required bool CompatibilityKnown { get; init; }

    public int? PriorImageMaxSchema { get; init; }

    public IReadOnlyList<AppliedSchemaMigration> Applied { get; init; } = [];

    public VerifiedPreMigrationBackup? Backup { get; init; }

    public string? BackupRefusalCode { get; init; }

    public bool RetargetConfigured { get; init; }

    public bool InPlaceDatabase { get; init; }
}

public sealed record RestoreDecision
{
    public required RestoreDecisionKind Kind { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public int? ExpectedSchema { get; init; }

    public string? BackupId { get; init; }
}

public sealed record RestoreCompletion
{
    public required RestoreDecisionKind Decision { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public string? OperationId { get; init; }

    public bool RestoreInvoked { get; init; }

    public bool SchemaMatched { get; init; }

    public bool ServingPointed { get; init; }

    public bool ImageStarted { get; init; }

    public bool ImageStartedBeforeSchemaMatch { get; init; }

    public bool ReadbackComplete { get; init; }

    public bool AuditPersisted { get; init; }
}

public static class ReaderIncompatibleRestorePolicy
{
    public const string RdsSnapshotEngine = "rds-snapshot";

    public const string ComposeDumpEngine = "compose-dump";

    public const string ExpandClassification = "expand";

    public static RestoreDecision Decide(RestoreAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (!assessment.CompatibilityKnown || assessment.PriorImageMaxSchema is null)
        {
            var ahead = assessment.CompatibilityKnown && assessment.PriorImageMaxSchema is null;
            return Refuse(
                ahead ? PreMigrationRestoreCodes.BackupSchemaAhead : PreMigrationRestoreCodes.ReadbackIncomplete,
                ahead
                    ? "The prior image has no schema number and the journal was read, so it was not started."
                    : "Reader compatibility is unknown, so the prior image was not started.");
        }

        var priorMax = assessment.PriorImageMaxSchema.Value;
        var restoreRequired = assessment.Applied.Any(migration => !OlderReaderCanAccept(migration, priorMax));
        if (!restoreRequired)
        {
            return new RestoreDecision { Kind = RestoreDecisionKind.NotRequired };
        }

        if (!assessment.InPlaceDatabase && !assessment.RetargetConfigured)
        {
            return Refuse(
                PreMigrationRestoreCodes.RetargetUnsupported,
                "This serving target has no connection secret the product can point at a restored database.");
        }

        if (assessment.Backup is null)
        {
            return Refuse(
                assessment.BackupRefusalCode ?? PreMigrationRestoreCodes.BackupRequired,
                "A verified pre-migration backup is required before the prior image can start.");
        }

        if (assessment.Backup.SchemaVersion > priorMax)
        {
            return Refuse(
                PreMigrationRestoreCodes.BackupSchemaAhead,
                "The backup schema is newer than the prior image can read.");
        }

        return new RestoreDecision
        {
            Kind = RestoreDecisionKind.RestoreBeforeStart,
            ExpectedSchema = assessment.Backup.SchemaVersion,
            BackupId = assessment.Backup.BackupId,
            Message = "Restore the verified backup and read back its schema before starting the prior image.",
        };
    }

    public static bool OlderReaderCanAccept(AppliedSchemaMigration migration, int priorMax)
    {
        ArgumentNullException.ThrowIfNull(migration);
        if (migration.Number <= priorMax)
        {
            return true;
        }

        return string.Equals(migration.Classification, ExpandClassification, StringComparison.Ordinal)
            && migration.MinReaderSchema <= priorMax;
    }

    public static bool SchemaMatchesBackup(
        int expectedSchema,
        int? observedSchema,
        IReadOnlyList<AppliedSchemaMigration> applied,
        int priorMax)
    {
        ArgumentNullException.ThrowIfNull(applied);
        if (observedSchema is null || observedSchema.Value != expectedSchema)
        {
            return false;
        }

        return applied.All(migration => OlderReaderCanAccept(migration, priorMax));
    }

    public static bool IsReportedSuccess(RestoreCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!completion.AuditPersisted || string.IsNullOrWhiteSpace(completion.OperationId))
        {
            return false;
        }

        if (completion.Decision == RestoreDecisionKind.NotRequired)
        {
            return completion.ImageStarted && !completion.ImageStartedBeforeSchemaMatch;
        }

        return completion.Decision == RestoreDecisionKind.RestoreBeforeStart
            && completion.RestoreInvoked
            && completion.SchemaMatched
            && completion.ServingPointed
            && completion.ImageStarted
            && !completion.ImageStartedBeforeSchemaMatch
            && completion.ReadbackComplete;
    }

    private static RestoreDecision Refuse(string code, string message)
        => new()
        {
            Kind = RestoreDecisionKind.Refuse,
            Code = code,
            Message = message,
        };
}
