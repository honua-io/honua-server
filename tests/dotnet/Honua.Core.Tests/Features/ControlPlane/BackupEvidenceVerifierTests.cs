// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Restore;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

public sealed class BackupEvidenceVerifierTests
{
    private const string BackupId = "backup-0123456789abcdef0123456789abcdef";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Compose_RefusesChecksumProjectDumpNameAndImage()
    {
        Verify(Evidence(hash: "f" + Hash[1..])).Code.Should().Be(PreMigrationRestoreCodes.BackupChecksumMismatch);
        Verify(Evidence(project: "other")).Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
        Verify(Evidence(dump: "dump.sql")).Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
        Verify(Evidence(image: "other-image")).Code.Should().Be(PreMigrationRestoreCodes.BackupImageMismatch);
        Verify(Evidence(id: "not-a-backup")).Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Compose_AcceptsAMatchingManifest()
    {
        var verification = Verify(Evidence());
        verification.Code.Should().BeNull();
        verification.Backup!.SchemaVersion.Should().Be(141);
        verification.Backup.Engine.Should().Be(ReaderIncompatibleRestorePolicy.ComposeDumpEngine);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Rds_RequiresAvailableStatusSchemaTagAndCellPrefix()
    {
        VerifyRds(status: "creating").Code.Should().Be(PreMigrationRestoreCodes.BackupUnverified);
        VerifyRds(schema: null).Code.Should().Be(PreMigrationRestoreCodes.BackupUnverified);
        VerifyRds(cell: false).Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
        VerifyRds(id: "other-snap").Code.Should().Be(PreMigrationRestoreCodes.ScopeViolation);
        VerifyRds(id: "rds:honua-cert-cert-postgres-2026-10-30").Code.Should().BeNull();
        VerifyRds().Backup!.SchemaVersion.Should().Be(141);
        BackupEvidenceVerifier.InstanceInCell("honua-cert-restore-ab12cd34", "honua-cert-").Should().BeTrue();
        BackupEvidenceVerifier.InstanceInCell("honua-certrun-job", "honua-cert-").Should().BeFalse();
    }

    private static BackupVerification Verify(ComposeBackupManifestEvidence evidence)
        => BackupEvidenceVerifier.VerifyCompose(evidence);

    private static BackupVerification VerifyRds(string? id = null, string status = "available", int? schema = 141, bool cell = true)
        => BackupEvidenceVerifier.VerifyRdsSnapshot(new RdsSnapshotEvidence
        {
            SnapshotId = id ?? "honua-cert-snap",
            CellPrefix = "honua-cert-",
            Status = status,
            SchemaVersion = schema,
            CellTagMatches = cell,
        });

    private static ComposeBackupManifestEvidence Evidence(
        string? id = null,
        string? project = null,
        string? image = null,
        string? hash = null,
        string? dump = null)
        => new()
        {
            BackupId = id ?? BackupId,
            Project = project ?? "honua",
            ExpectedProject = "honua",
            Image = image ?? "image@sha256:abc",
            ExpectedImage = "image@sha256:abc",
            SchemaVersion = 141,
            Sha256 = hash ?? Hash,
            ComputedSha256 = Hash,
            DumpFileName = dump ?? BackupEvidenceVerifier.DumpFileName,
        };
}
