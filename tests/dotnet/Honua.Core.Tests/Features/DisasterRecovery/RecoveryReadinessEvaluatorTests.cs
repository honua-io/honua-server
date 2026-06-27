// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.DisasterRecovery.Domain;

namespace Honua.Core.Tests.Features.DisasterRecovery;

/// <summary>
/// Unit tests for the recovery-readiness evaluator (#356). These assert the shared
/// RTO/RPO posture rules the admin reporting surface and provider implementations depend on.
/// </summary>
public sealed class RecoveryReadinessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly RecoveryObjectives Objectives =
        new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));

    [Fact]
    public void Evaluate_NoBackups_IsNotReady()
    {
        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, [], Now);

        readiness.Level.Should().Be(RecoveryReadinessLevel.NotReady);
        readiness.LastSuccessfulBaseBackup.Should().BeNull();
        readiness.RestorablePoint.Should().BeNull();
        readiness.DataLossWindow.Should().BeNull();
        readiness.RecoveryPointObjectiveMet.Should().BeFalse();
        readiness.AssessedAt.Should().Be(Now);
    }

    [Fact]
    public void Evaluate_OnlyWalArchives_IsNotReady_BecauseNoBaseAnchor()
    {
        // WAL alone cannot rebuild a cluster — a base backup is required to anchor recovery.
        var backups = new[]
        {
            new BackupRecord("wal-1", BackupKind.PostgresWal, Now.AddMinutes(-1), Succeeded: true),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.Level.Should().Be(RecoveryReadinessLevel.NotReady);
        readiness.LastSuccessfulBaseBackup.Should().BeNull();
    }

    [Fact]
    public void Evaluate_FailedBaseBackup_IsIgnored()
    {
        var backups = new[]
        {
            new BackupRecord("base-1", BackupKind.PostgresBase, Now.AddMinutes(-2), Succeeded: false, FailureReason: "disk full"),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.Level.Should().Be(RecoveryReadinessLevel.NotReady);
    }

    [Fact]
    public void Evaluate_RecentBaseBackupWithinRpo_IsReady()
    {
        var backups = new[]
        {
            new BackupRecord("base-1", BackupKind.PostgresBase, Now.AddMinutes(-2), Succeeded: true, SizeBytes: 1024),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.Level.Should().Be(RecoveryReadinessLevel.Ready);
        readiness.LastSuccessfulBaseBackup.Should().Be(Now.AddMinutes(-2));
        readiness.RestorablePoint.Should().Be(Now.AddMinutes(-2));
        readiness.DataLossWindow.Should().Be(TimeSpan.FromMinutes(2));
        readiness.RecoveryPointObjectiveMet.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WalArchiveExtendsRestorablePointInsideRpo_IsReady()
    {
        // Base backup is older than the RPO, but a recent WAL archive shrinks the data-loss
        // window back inside the objective — point-in-time recovery is sustained.
        var backups = new[]
        {
            new BackupRecord("base-1", BackupKind.PostgresBase, Now.AddHours(-6), Succeeded: true),
            new BackupRecord("wal-1", BackupKind.PostgresWal, Now.AddMinutes(-3), Succeeded: true),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.Level.Should().Be(RecoveryReadinessLevel.Ready);
        readiness.LastSuccessfulBaseBackup.Should().Be(Now.AddHours(-6));
        readiness.RestorablePoint.Should().Be(Now.AddMinutes(-3));
        readiness.DataLossWindow.Should().Be(TimeSpan.FromMinutes(3));
        readiness.RecoveryPointObjectiveMet.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RestorablePointOlderThanRpo_IsAtRisk()
    {
        var backups = new[]
        {
            new BackupRecord("base-1", BackupKind.PostgresBase, Now.AddMinutes(-30), Succeeded: true),
            new BackupRecord("wal-1", BackupKind.PostgresWal, Now.AddMinutes(-20), Succeeded: true),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.Level.Should().Be(RecoveryReadinessLevel.AtRisk);
        readiness.RestorablePoint.Should().Be(Now.AddMinutes(-20));
        readiness.DataLossWindow.Should().Be(TimeSpan.FromMinutes(20));
        readiness.RecoveryPointObjectiveMet.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_RedisSnapshot_DoesNotExtendRestorablePoint()
    {
        // A Redis snapshot warms a cache; it does not protect committed data, so it must not
        // count toward the restorable point.
        var backups = new[]
        {
            new BackupRecord("base-1", BackupKind.PostgresBase, Now.AddMinutes(-30), Succeeded: true),
            new BackupRecord("redis-1", BackupKind.RedisSnapshot, Now.AddMinutes(-1), Succeeded: true),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.RestorablePoint.Should().Be(Now.AddMinutes(-30));
        readiness.Level.Should().Be(RecoveryReadinessLevel.AtRisk);
    }

    [Fact]
    public void Evaluate_ClampsNegativeWindowToZero_WhenBackupRecordedInFuture()
    {
        var backups = new[]
        {
            new BackupRecord("base-1", BackupKind.PostgresBase, Now.AddMinutes(5), Succeeded: true),
        };

        var readiness = RecoveryReadinessEvaluator.Evaluate(Objectives, backups, Now);

        readiness.DataLossWindow.Should().Be(TimeSpan.Zero);
        readiness.Level.Should().Be(RecoveryReadinessLevel.Ready);
    }

    [Fact]
    public void Evaluate_NullArguments_Throw()
    {
        var act1 = () => RecoveryReadinessEvaluator.Evaluate(null!, [], Now);
        var act2 = () => RecoveryReadinessEvaluator.Evaluate(Objectives, null!, Now);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }
}
