// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// Pure, deterministic evaluator that derives a <see cref="RecoveryReadiness"/> assessment
/// from a set of recorded backups and the configured recovery objectives. Keeping the
/// recovery-posture logic in Core (independent of any storage or scheduling backend) lets
/// both the admin reporting surface and the provider implementations share one definition of
/// "are we recoverable inside our objectives?" (#356).
/// </summary>
public static class RecoveryReadinessEvaluator
{
    /// <summary>
    /// Evaluates recovery readiness as of <paramref name="asOf"/>.
    /// </summary>
    /// <param name="objectives">The recovery objectives to measure against.</param>
    /// <param name="backups">Recorded backups; order is irrelevant and failed records are ignored for restorability.</param>
    /// <param name="asOf">The reference time the data-loss window is measured against.</param>
    /// <returns>A readiness assessment.</returns>
    /// <remarks>
    /// Readiness rules:
    /// <list type="bullet">
    ///   <item><description>
    ///     No successful base backup → <see cref="RecoveryReadinessLevel.NotReady"/>; the
    ///     system cannot be recovered at all.
    ///   </description></item>
    ///   <item><description>
    ///     A base backup exists but the data-loss window (age of the latest successful
    ///     data-protecting backup) exceeds the recovery point objective →
    ///     <see cref="RecoveryReadinessLevel.AtRisk"/>.
    ///   </description></item>
    ///   <item><description>
    ///     A base backup exists and the data-loss window is within the recovery point
    ///     objective → <see cref="RecoveryReadinessLevel.Ready"/>.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static RecoveryReadiness Evaluate(
        RecoveryObjectives objectives,
        IReadOnlyList<BackupRecord> backups,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        ArgumentNullException.ThrowIfNull(backups);

        DateTimeOffset? lastBaseBackup = null;
        DateTimeOffset? restorablePoint = null;

        foreach (var backup in backups)
        {
            if (!backup.Succeeded)
            {
                continue;
            }

            if (backup.Kind == BackupKind.PostgresBase &&
                (lastBaseBackup is null || backup.CompletedAt > lastBaseBackup.Value))
            {
                lastBaseBackup = backup.CompletedAt;
            }

            if (backup.IsDataProtecting &&
                (restorablePoint is null || backup.CompletedAt > restorablePoint.Value))
            {
                restorablePoint = backup.CompletedAt;
            }
        }

        // Without a base backup there is nothing to restore from — archived WAL alone cannot
        // rebuild a cluster — so the restorable point is only meaningful with a base anchor.
        if (lastBaseBackup is null)
        {
            return new RecoveryReadiness(
                Level: RecoveryReadinessLevel.NotReady,
                Objectives: objectives,
                LastSuccessfulBaseBackup: null,
                RestorablePoint: null,
                DataLossWindow: null,
                RecoveryPointObjectiveMet: false,
                AssessedAt: asOf);
        }

        // The restorable point is the latest data-protecting backup; it is never older than
        // the base backup because the base backup itself is data-protecting.
        var dataLossWindow = asOf - restorablePoint!.Value;
        if (dataLossWindow < TimeSpan.Zero)
        {
            dataLossWindow = TimeSpan.Zero;
        }

        var rpoMet = dataLossWindow <= objectives.RecoveryPointObjective;

        return new RecoveryReadiness(
            Level: rpoMet ? RecoveryReadinessLevel.Ready : RecoveryReadinessLevel.AtRisk,
            Objectives: objectives,
            LastSuccessfulBaseBackup: lastBaseBackup,
            RestorablePoint: restorablePoint,
            DataLossWindow: dataLossWindow,
            RecoveryPointObjectiveMet: rpoMet,
            AssessedAt: asOf);
    }
}
