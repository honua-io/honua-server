// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.ControlPlane.Restore;

public sealed record ComposeBackupManifestEvidence
{
    public required string BackupId { get; init; }

    public required string Project { get; init; }

    public required string ExpectedProject { get; init; }

    public required string Image { get; init; }

    public required string ExpectedImage { get; init; }

    public required int SchemaVersion { get; init; }

    public required string Sha256 { get; init; }

    public required string ComputedSha256 { get; init; }

    public required string DumpFileName { get; init; }
}

public sealed record RdsSnapshotEvidence
{
    public required string SnapshotId { get; init; }

    public required string CellPrefix { get; init; }

    public required string Status { get; init; }

    public int? SchemaVersion { get; init; }

    public required bool CellTagMatches { get; init; }
}

public static partial class BackupEvidenceVerifier
{
    public const string DumpFileName = "database.dump";

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

    public static bool InstanceInCell(string instanceId, string cellPrefix)
        => SnapshotInCell(instanceId, cellPrefix) && RdsIdentifier().IsMatch(instanceId);

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

    public static bool IsValidBackupId(string backupId) => BackupIdPattern().IsMatch(backupId);

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RdsIdentifier();

    [GeneratedRegex(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex();
}

public sealed record BackupVerification
{
    public VerifiedPreMigrationBackup? Backup { get; init; }

    public string? Code { get; init; }

    public static BackupVerification Accept(VerifiedPreMigrationBackup backup)
        => new() { Backup = backup };

    public static BackupVerification Refuse(string code)
        => new() { Code = code };
}
