// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Restore;

/// <summary>
/// Stable codes for a rollback across a reader-incompatible migration. A caller reports success
/// only through <see cref="ReaderIncompatibleRestorePolicy.IsReportedSuccess"/>.
/// </summary>
public static class PreMigrationRestoreCodes
{
    /// <summary>The prior image cannot read the schema and no backup was named or found.</summary>
    public const string BackupRequired = "restore-backup-required";

    /// <summary>A backup was named but its checksum, status, or scope did not verify.</summary>
    public const string BackupUnverified = "restore-backup-unverified";

    /// <summary>The backup bytes do not match the recorded checksum.</summary>
    public const string BackupChecksumMismatch = "restore-backup-checksum-mismatch";

    /// <summary>The backup was taken for a different image than the one being started.</summary>
    public const string BackupImageMismatch = "restore-backup-image-mismatch";

    /// <summary>The backup schema is newer than the prior image can read, or that image has no schema.</summary>
    public const string BackupSchemaAhead = "restore-backup-schema-ahead";

    /// <summary>The backup names a database, project, or snapshot outside this cell.</summary>
    public const string ScopeViolation = "restore-scope-violation";

    /// <summary>Schema, image, or customer readback did not complete.</summary>
    public const string ReadbackIncomplete = "restore-readback-incomplete";

    /// <summary>Restore finished but the database is not the backup's schema.</summary>
    public const string RestoreIncomplete = "restore-incomplete";

    /// <summary>The database provider refused or did not finish the restore.</summary>
    public const string ProviderFailed = "restore-provider-failed";

    /// <summary>This serving target cannot be pointed at the restored database.</summary>
    public const string RetargetUnsupported = "restore-retarget-unsupported";

    /// <summary>The durable audit record was not written.</summary>
    public const string AuditFailed = "restore-audit-failed";

    /// <summary>The local Compose restore transport is not configured, so the prior image was not started.</summary>
    public const string TransportUnavailable = "restore-transport-unavailable";

    /// <summary>The deploy target is not in the server catalog.</summary>
    public const string TargetUnknown = "restore-target-unknown";
}

/// <summary>How a rollback relates to the current schema.</summary>
public enum RestoreDecisionKind
{
    /// <summary>The prior image can read the schema. No restore is required.</summary>
    NotRequired = 0,

    /// <summary>A verified backup must be restored before the prior image starts.</summary>
    RestoreBeforeStart = 1,

    /// <summary>The rollback must not start the prior image.</summary>
    Refuse = 2,
}

/// <summary>One applied migration the journal reported to the restore policy.</summary>
/// <param name="Number">Migration number parsed from the script name.</param>
/// <param name="Classification">Declaration classification, or null when none was recorded.</param>
/// <param name="MinReaderSchema">Oldest reader schema an expand declaration allows.</param>
public sealed record AppliedSchemaMigration(int Number, string? Classification, int MinReaderSchema);

/// <summary>
/// Backup the product has already checked. Callers must not construct this from unverified input.
/// </summary>
/// <param name="BackupId">Snapshot id or compose backup id.</param>
/// <param name="SchemaVersion">Schema number recorded when the backup was taken.</param>
/// <param name="Engine"><c>rds-snapshot</c> or <c>compose-dump</c>.</param>
public sealed record VerifiedPreMigrationBackup(string BackupId, int SchemaVersion, string Engine);

/// <summary>Inputs to <see cref="ReaderIncompatibleRestorePolicy.Decide"/>.</summary>
public sealed record RestoreAssessment
{
    /// <summary>Whether the journal and the prior image schema were both read.</summary>
    public required bool CompatibilityKnown { get; init; }

    /// <summary>Highest migration the prior image ships, when its label or catalog entry was read.</summary>
    public int? PriorImageMaxSchema { get; init; }

    /// <summary>Applied migrations. Empty when <see cref="CompatibilityKnown"/> is false.</summary>
    public IReadOnlyList<AppliedSchemaMigration> Applied { get; init; } = [];

    /// <summary>Verified backup, when one passed the engine checks.</summary>
    public VerifiedPreMigrationBackup? Backup { get; init; }

    /// <summary>Why the named backup was rejected. Null when no backup was supplied.</summary>
    public string? BackupRefusalCode { get; init; }

    /// <summary>Whether a cloud target has a connection secret the product can repoint.</summary>
    public bool RetargetConfigured { get; init; }

    /// <summary>True for an in-place database (local Compose). False when serving must move to a new instance.</summary>
    public bool InPlaceDatabase { get; init; }
}

/// <summary>Result of <see cref="ReaderIncompatibleRestorePolicy.Decide"/>.</summary>
public sealed record RestoreDecision
{
    /// <summary>What the executor must do.</summary>
    public required RestoreDecisionKind Kind { get; init; }

    /// <summary>Stable code when <see cref="Kind"/> is <see cref="RestoreDecisionKind.Refuse"/>.</summary>
    public string? Code { get; init; }

    /// <summary>Short operator text. No connection strings or customer bodies.</summary>
    public string? Message { get; init; }

    /// <summary>Schema the restored database must show before the prior image starts.</summary>
    public int? ExpectedSchema { get; init; }

    /// <summary>Backup the executor must restore, when <see cref="Kind"/> is restore.</summary>
    public string? BackupId { get; init; }
}

/// <summary>
/// Flags collected after an attempt. <see cref="ReaderIncompatibleRestorePolicy.IsReportedSuccess"/>
/// is the only success predicate.
/// </summary>
public sealed record RestoreCompletion
{
    /// <summary>Decision that governed the attempt.</summary>
    public required RestoreDecisionKind Decision { get; init; }

    /// <summary>Stable code when the attempt did not succeed.</summary>
    public string? Code { get; init; }

    /// <summary>Short operator text.</summary>
    public string? Message { get; init; }

    /// <summary>Durable operation id. Present only when success is reported.</summary>
    public string? OperationId { get; init; }

    /// <summary>The provider restore ran.</summary>
    public bool RestoreInvoked { get; init; }

    /// <summary>Live schema readback matched the backup.</summary>
    public bool SchemaMatched { get; init; }

    /// <summary>Serving points at the restored database, or the database was restored in place.</summary>
    public bool ServingPointed { get; init; }

    /// <summary>The prior image was started.</summary>
    public bool ImageStarted { get; init; }

    /// <summary>The prior image was started before schema readback matched.</summary>
    public bool ImageStartedBeforeSchemaMatch { get; init; }

    /// <summary>Customer or health readback completed after the image was up.</summary>
    public bool ReadbackComplete { get; init; }

    /// <summary>An audit line was durable before this result was returned.</summary>
    public bool AuditPersisted { get; init; }
}

/// <summary>
/// Shared rule for rolling back to an image that cannot read the current schema. Compose and
/// cloud executors both call this so a reader-incompatible migration such as 142 is restore-then-start,
/// never an image swap. Success is impossible when restore, schema readback, retarget, or audit is missing.
/// </summary>
public static class ReaderIncompatibleRestorePolicy
{
    /// <summary>Engine id for an RDS snapshot restored to a new instance.</summary>
    public const string RdsSnapshotEngine = "rds-snapshot";

    /// <summary>Engine id for a local Compose dump restored in place.</summary>
    public const string ComposeDumpEngine = "compose-dump";

    /// <summary>Declaration classification an older reader may accept.</summary>
    public const string ExpandClassification = "expand";

    /// <summary>
    /// Decides whether rollback must restore a verified pre-migration backup before the prior image starts.
    /// </summary>
    /// <param name="assessment">Journal, image schema, and backup verification.</param>
    /// <returns>A refuse, restore, or not-required decision. Restore is never implied by a missing check.</returns>
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

    /// <summary>
    /// Whether an applied migration is readable by an image whose highest shipped migration is
    /// <paramref name="priorMax"/>.
    /// </summary>
    /// <param name="migration">Applied journal entry.</param>
    /// <param name="priorMax">Prior image schema.</param>
    /// <returns>True when the prior image can serve with this migration applied.</returns>
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

    /// <summary>
    /// Whether live readback shows the backup schema and no migration the prior image cannot read.
    /// </summary>
    /// <param name="expectedSchema">Schema recorded on the verified backup.</param>
    /// <param name="observedSchema">Highest migration number read from the restored database.</param>
    /// <param name="applied">Migrations read from the restored database.</param>
    /// <param name="priorMax">Prior image schema.</param>
    /// <returns>True only when the database matches the backup and is readable by the prior image.</returns>
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

    /// <summary>
    /// Success requires a restore when one was required, schema match before image start, serving
    /// pointed at that database, completed readback, and a durable audit line. Any gap is failure.
    /// </summary>
    /// <param name="completion">Flags the executor collected.</param>
    /// <returns>True only when every required flag is set and the image was not started early.</returns>
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
