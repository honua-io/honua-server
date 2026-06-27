// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// Category of a recorded backup artifact. The kinds map to the PostgreSQL backup
/// strategy described in the backup-and-restore runbook: full base backups taken with
/// <c>pg_basebackup</c>, continuous WAL segments archived between base backups, and Redis
/// cache-state snapshots used to warm a failover target (#356, ADR-0024).
/// </summary>
public enum BackupKind
{
    /// <summary>
    /// A full PostgreSQL base backup taken with <c>pg_basebackup</c>. A base backup is the
    /// anchor from which point-in-time recovery replays archived WAL.
    /// </summary>
    [JsonStringEnumMemberName("postgres_base")]
    PostgresBase = 0,

    /// <summary>
    /// An archived PostgreSQL write-ahead-log segment. WAL archives extend the restorable
    /// point forward from the most recent base backup, shrinking the recovery-point window.
    /// </summary>
    [JsonStringEnumMemberName("postgres_wal")]
    PostgresWal = 1,

    /// <summary>
    /// A Redis cache-state snapshot used to pre-warm a failover target's distributed cache.
    /// </summary>
    [JsonStringEnumMemberName("redis_snapshot")]
    RedisSnapshot = 2
}

/// <summary>
/// Overall recovery-readiness level derived from recorded backups against the configured
/// recovery objectives. Used by the admin observability surface to render a single
/// at-a-glance disaster-recovery posture.
/// </summary>
public enum RecoveryReadinessLevel
{
    /// <summary>
    /// No successful base backup exists, so the system cannot be recovered. This is the
    /// most severe state and always requires operator intervention.
    /// </summary>
    [JsonStringEnumMemberName("not_ready")]
    NotReady = 0,

    /// <summary>
    /// A base backup exists, but the data-loss window exceeds the configured recovery point
    /// objective — a recovery would lose more data than the objective allows.
    /// </summary>
    [JsonStringEnumMemberName("at_risk")]
    AtRisk = 1,

    /// <summary>
    /// A base backup exists and the data-loss window is within the configured recovery point
    /// objective. The system is recoverable inside its objectives.
    /// </summary>
    [JsonStringEnumMemberName("ready")]
    Ready = 2
}
