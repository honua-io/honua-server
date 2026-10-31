// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

public sealed class ReaderIncompatibleRestorePolicyTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Migration142_IsReaderIncompatibleForAnImageAt141()
    {
        var path = FindRepoFile(Path.Combine("src", "Honua.Server", "Migrations", "142_CreateAiFinancialAccounting.sql"));
        var declaration = MigrationReaderCompatibility.Declare("142_CreateAiFinancialAccounting.sql", File.ReadAllText(path));
        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.ReaderIncompatible);

        var decision = ReaderIncompatibleRestorePolicy.Decide(Assessment(
            priorMax: 141,
            applied: [Migration(141, "expand"), Migration(142, declaration.Classification)],
            backup: null,
            retarget: true));

        decision.Kind.Should().Be(RestoreDecisionKind.Refuse);
        decision.Code.Should().Be(PreMigrationRestoreCodes.BackupRequired);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ExpandAboveThePriorImage_DoesNotRequireRestore()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(Assessment(
            priorMax: 141,
            applied: [Migration(141, "expand"), Migration(142, "expand", minReader: 141)],
            backup: null,
            retarget: false));

        decision.Kind.Should().Be(RestoreDecisionKind.NotRequired);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void UnknownJournal_RefusesBeforeAnyImageStart()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(new RestoreAssessment
        {
            CompatibilityKnown = false,
            PriorImageMaxSchema = 141,
            RetargetConfigured = true,
        });

        decision.Kind.Should().Be(RestoreDecisionKind.Refuse);
        decision.Code.Should().Be(PreMigrationRestoreCodes.ReadbackIncomplete);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void KnownJournalWithoutPriorSchema_IsSchemaAhead()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(Assessment(
            priorMax: null,
            applied: [Migration(142, "reader-incompatible")],
            backup: null,
            retarget: true));

        decision.Code.Should().Be(PreMigrationRestoreCodes.BackupSchemaAhead);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void CloudTargetWithoutASecret_RefusesBeforeRestore()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(Assessment(
            priorMax: 141,
            applied: [Migration(142, "reader-incompatible")],
            backup: Verified("snap", 141),
            retarget: false));

        decision.Code.Should().Be(PreMigrationRestoreCodes.RetargetUnsupported);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BackupNewerThanThePriorImage_IsSchemaAhead()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(Assessment(
            priorMax: 141,
            applied: [Migration(142, "reader-incompatible")],
            backup: Verified("snap", 142),
            retarget: true));

        decision.Code.Should().Be(PreMigrationRestoreCodes.BackupSchemaAhead);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void VerifiedBackup_RestoresBeforeStart()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(Assessment(
            priorMax: 141,
            applied: [Migration(142, "reader-incompatible")],
            backup: Verified("honua-cert-snap", 141),
            retarget: true));

        decision.Kind.Should().Be(RestoreDecisionKind.RestoreBeforeStart);
        decision.ExpectedSchema.Should().Be(141);
        decision.BackupId.Should().Be("honua-cert-snap");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void InPlaceDatabase_CanRestoreWithoutAConnectionSecret()
    {
        var decision = ReaderIncompatibleRestorePolicy.Decide(new RestoreAssessment
        {
            CompatibilityKnown = true,
            PriorImageMaxSchema = 141,
            Applied = [Migration(142, "reader-incompatible")],
            Backup = Verified("backup-" + new string('a', 32), 141, ReaderIncompatibleRestorePolicy.ComposeDumpEngine),
            RetargetConfigured = false,
            InPlaceDatabase = true,
        });

        decision.Kind.Should().Be(RestoreDecisionKind.RestoreBeforeStart);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void SuccessRequiresAuditSchemaMatchAndNoEarlyStart()
    {
        var complete = Completion(audit: true, operationId: "op-1", schema: true, readback: true, startedEarly: false);
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(complete).Should().BeTrue();

        ReaderIncompatibleRestorePolicy.IsReportedSuccess(complete with { AuditPersisted = false }).Should().BeFalse();
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(complete with { SchemaMatched = false }).Should().BeFalse();
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(complete with { ReadbackComplete = false }).Should().BeFalse();
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(complete with { ImageStartedBeforeSchemaMatch = true }).Should().BeFalse();
        ReaderIncompatibleRestorePolicy.IsReportedSuccess(complete with { OperationId = " " }).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void SchemaReadback_RejectsADatabaseThatStillHasTheUnreadableMigration()
    {
        var applied = new List<AppliedSchemaMigration> { Migration(141, "expand"), Migration(142, "reader-incompatible") };
        ReaderIncompatibleRestorePolicy.SchemaMatchesBackup(141, 142, applied, 141).Should().BeFalse();
        ReaderIncompatibleRestorePolicy.SchemaMatchesBackup(141, 141, [Migration(141, "expand")], 141).Should().BeTrue();
    }

    private static RestoreAssessment Assessment(int? priorMax, IReadOnlyList<AppliedSchemaMigration> applied, VerifiedPreMigrationBackup? backup, bool retarget)
        => new()
        {
            CompatibilityKnown = true,
            PriorImageMaxSchema = priorMax,
            Applied = applied,
            Backup = backup,
            RetargetConfigured = retarget,
        };

    private static AppliedSchemaMigration Migration(int number, string classification, int minReader = 0)
        => new(number, classification, minReader);

    private static VerifiedPreMigrationBackup Verified(string id, int schema, string? engine = null)
        => new(id, schema, engine ?? ReaderIncompatibleRestorePolicy.RdsSnapshotEngine);

    private static RestoreCompletion Completion(bool audit, string operationId, bool schema, bool readback, bool startedEarly)
        => new()
        {
            Decision = RestoreDecisionKind.RestoreBeforeStart,
            OperationId = operationId,
            RestoreInvoked = true,
            SchemaMatched = schema,
            ServingPointed = true,
            ImageStarted = true,
            ImageStartedBeforeSchemaMatch = startedEarly,
            ReadbackComplete = readback,
            AuditPersisted = audit,
        };

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
