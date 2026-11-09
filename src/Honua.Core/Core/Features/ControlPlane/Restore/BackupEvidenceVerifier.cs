// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.ControlPlane.Restore;

/// <summary>Facts read from a Compose backup directory before any restore command runs.</summary>
public sealed record ComposeBackupManifestEvidence
{
    /// <summary>Directory name. Must be <c>backup-</c> plus 32 lowercase hex digits.</summary>
    public required string BackupId { get; init; }

    /// <summary>Project recorded in the manifest.</summary>
    public required string Project { get; init; }

    /// <summary>Project the operator configured. Never taken from the model request.</summary>
    public required string ExpectedProject { get; init; }

    /// <summary>Image id recorded in the manifest.</summary>
    public required string Image { get; init; }

    /// <summary>Prior image the rollback asked to start.</summary>
    public required string ExpectedImage { get; init; }

    /// <summary>Schema recorded when the dump was taken.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Checksum recorded in the manifest, lowercase hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Checksum of the dump bytes the product just hashed.</summary>
    public required string ComputedSha256 { get; init; }

    /// <summary>Dump file name. Only <c>database.dump</c> is accepted.</summary>
    public required string DumpFileName { get; init; }
}

/// <summary>Facts read from RDS before a snapshot is restored.</summary>
public sealed record RdsSnapshotEvidence
{
    /// <summary>Snapshot identifier, not an ARN.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Cell name prefix the controller is allowed to touch.</summary>
    public required string CellPrefix { get; init; }

    /// <summary>Provider status. Only <c>available</c> verifies.</summary>
    public required string Status { get; init; }

    /// <summary>Schema tag recorded when the snapshot was taken. Null when the tag is missing.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Whether the snapshot's cell tag equals <see cref="CellPrefix"/>.</summary>
    public required bool CellTagMatches { get; init; }
}

/// <summary>
/// Checks a backup belongs to this cell and matches its bytes or snapshot status.
/// Compose and RDS callers share this so a model-supplied id cannot widen scope.
/// </summary>
public static partial class BackupEvidenceVerifier
{
    /// <summary>Dump file name. No other name is hashed or restored.</summary>
    public const string DumpFileName = "database.dump";

    /// <summary>
    /// Verifies a Compose manifest. A mismatch returns a refusal code and no verified backup.
    /// </summary>
    /// <param name="evidence">Manifest fields and the hash of the dump.</param>
    /// <returns>A verified backup, or a refusal code.</returns>
    public static BackupVerification VerifyCompose(ComposeBackupManifestEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!BackupIdPattern().IsMatch(evidence.BackupId))
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.ScopeViolation);
        }

        if (!string.Equals(evidence.DumpFileName, DumpFileName, StringComparison.Ordinal))
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.ScopeViolation);
        }

        if (!string.Equals(evidence.Project, evidence.ExpectedProject, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(evidence.ExpectedProject))
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.ScopeViolation);
        }

        if (!string.Equals(evidence.Image, evidence.ExpectedImage, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(evidence.ExpectedImage))
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.BackupImageMismatch);
        }

        if (!IsLowerHex(evidence.Sha256)
            || !string.Equals(evidence.Sha256, evidence.ComputedSha256, StringComparison.Ordinal))
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.BackupChecksumMismatch);
        }

        return BackupVerification.Accept(new VerifiedPreMigrationBackup(
            evidence.BackupId,
            evidence.SchemaVersion,
            ReaderIncompatibleRestorePolicy.ComposeDumpEngine));
    }

    /// <summary>
    /// Verifies an RDS snapshot is available, tagged for this cell, and named inside the cell prefix.
    /// </summary>
    /// <param name="evidence">DescribeDBSnapshots fields the product read. Not caller-asserted.</param>
    /// <returns>A verified backup, or a refusal code.</returns>
    public static BackupVerification VerifyRdsSnapshot(RdsSnapshotEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!SnapshotInCell(evidence.SnapshotId, evidence.CellPrefix) || !evidence.CellTagMatches)
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.ScopeViolation);
        }

        if (!string.Equals(evidence.Status, "available", StringComparison.Ordinal))
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.BackupUnverified);
        }

        if (evidence.SchemaVersion is null)
        {
            return BackupVerification.Refuse(PreMigrationRestoreCodes.BackupUnverified);
        }

        return BackupVerification.Accept(new VerifiedPreMigrationBackup(
            evidence.SnapshotId,
            evidence.SchemaVersion.Value,
            ReaderIncompatibleRestorePolicy.RdsSnapshotEngine));
    }

    /// <summary>Whether a new database identifier stays inside the cell prefix and is a legal RDS id.</summary>
    /// <param name="instanceId">Proposed instance identifier.</param>
    /// <param name="cellPrefix">Configured cell prefix.</param>
    /// <returns>True when the id is prefixed and contains only lowercase RDS identifier characters.</returns>
    public static bool InstanceInCell(string instanceId, string cellPrefix)
        => SnapshotInCell(instanceId, cellPrefix) && RdsIdentifier().IsMatch(instanceId);

    /// <summary>Whether a snapshot id is an automated or manual snapshot of this cell.</summary>
    /// <param name="snapshotId">Snapshot identifier.</param>
    /// <param name="cellPrefix">Configured cell prefix.</param>
    /// <returns>True when the id starts with the prefix or with <c>rds:</c> plus the prefix.</returns>
    public static bool SnapshotInCell(string snapshotId, string cellPrefix)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || string.IsNullOrWhiteSpace(cellPrefix))
        {
            return false;
        }

        if (snapshotId.Contains('/', StringComparison.Ordinal) || snapshotId.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return snapshotId.StartsWith(cellPrefix, StringComparison.Ordinal)
            || snapshotId.StartsWith("rds:" + cellPrefix, StringComparison.Ordinal);
    }

    private static bool IsLowerHex(string value)
        => value.Length == 64 && LowerHex().IsMatch(value);

    [GeneratedRegex(@"^backup-[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupIdPattern();

    /// <summary>Whether a backup identifier is safe to use as a single directory name.</summary>
    public static bool IsValidBackupId(string backupId) => BackupIdPattern().IsMatch(backupId);

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RdsIdentifier();

    [GeneratedRegex(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex();
}

/// <summary>Either a verified backup or a stable refusal code.</summary>
public sealed record BackupVerification
{
    /// <summary>Verified backup when <see cref="Code"/> is null.</summary>
    public VerifiedPreMigrationBackup? Backup { get; init; }

    /// <summary>Refusal code when <see cref="Backup"/> is null.</summary>
    public string? Code { get; init; }

    /// <summary>Builds an accepting verification.</summary>
    /// <param name="backup">Backup that passed the engine checks.</param>
    /// <returns>An accepting verification.</returns>
    public static BackupVerification Accept(VerifiedPreMigrationBackup backup)
        => new() { Backup = backup };

    /// <summary>Builds a refusing verification.</summary>
    /// <param name="code">Stable code.</param>
    /// <returns>A refusing verification.</returns>
    public static BackupVerification Refuse(string code)
        => new() { Code = code };
}
