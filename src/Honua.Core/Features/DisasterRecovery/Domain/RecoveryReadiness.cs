// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// A point-in-time assessment of disaster-recovery readiness, derived from recorded backups
/// against the configured <see cref="RecoveryObjectives"/>. This is the projection the admin
/// observability surface renders for the RTO/RPO reporting capability (#356).
/// </summary>
/// <param name="Level">Overall readiness level.</param>
/// <param name="Objectives">The objectives the assessment was measured against.</param>
/// <param name="LastSuccessfulBaseBackup">When the most recent successful base backup completed; null when none exists.</param>
/// <param name="RestorablePoint">
/// The most recent restorable point — the latest successful data-protecting backup (base
/// backup or archived WAL). Null when no successful base backup exists.
/// </param>
/// <param name="DataLossWindow">
/// Estimated data that would be lost on recovery, measured as the age of the
/// <paramref name="RestorablePoint"/>. Null when no restorable point exists.
/// </param>
/// <param name="RecoveryPointObjectiveMet">
/// True when the <paramref name="DataLossWindow"/> is within the configured recovery point
/// objective. False when there is no restorable point or the window exceeds the objective.
/// </param>
/// <param name="AssessedAt">When the assessment was computed.</param>
public sealed record RecoveryReadiness(
    RecoveryReadinessLevel Level,
    RecoveryObjectives Objectives,
    DateTimeOffset? LastSuccessfulBaseBackup,
    DateTimeOffset? RestorablePoint,
    TimeSpan? DataLossWindow,
    bool RecoveryPointObjectiveMet,
    DateTimeOffset AssessedAt);
