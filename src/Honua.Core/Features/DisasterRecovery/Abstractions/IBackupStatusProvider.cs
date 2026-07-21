// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.DisasterRecovery.Domain;

namespace Honua.Core.Features.DisasterRecovery.Abstractions;

/// <summary>
/// Provides recorded backup history and a recovery-readiness assessment for the disaster
/// recovery reporting surface. Implementations (for example a PostgreSQL backup service that
/// schedules <c>pg_basebackup</c> and WAL archiving) supply the recorded backups and compute
/// readiness against the configured <see cref="Domain.RecoveryObjectives"/> (#356, ADR-0024).
/// This interface has no implementation in this server today — Honua Server's backup/failover
/// posture is owned by the deployment's infrastructure/managed-database layer (see the
/// <c>dr.*</c> entries in <c>capability-no-surface-allowlist.v1.json</c>); a former shared pure
/// evaluator (<c>RecoveryReadinessEvaluator</c>) was removed as unused dead code in #2946 and can
/// be resurrected from git history if a concrete <see cref="IBackupStatusProvider"/> is ever built.
/// </summary>
public interface IBackupStatusProvider
{
    /// <summary>
    /// Returns the recorded backups known to this provider, most recent first is not required.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<IReadOnlyList<BackupRecord>> GetBackupHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current recovery-readiness assessment measured against the configured
    /// recovery objectives.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<RecoveryReadiness> GetRecoveryReadinessAsync(CancellationToken cancellationToken = default);
}
