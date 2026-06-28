// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.DisasterRecovery.Domain;

namespace Honua.Core.Features.DisasterRecovery.Abstractions;

/// <summary>
/// Provides recorded backup history and a recovery-readiness assessment for the disaster
/// recovery reporting surface. Implementations (for example a PostgreSQL backup service that
/// schedules <c>pg_basebackup</c> and WAL archiving) supply the recorded backups; readiness is
/// derived through <see cref="Domain.RecoveryReadinessEvaluator"/> so the posture definition
/// stays shared (#356, ADR-0024).
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
