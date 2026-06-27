// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// Backup automation cadence for a disaster-recovery plan. The schedule governs how often a
/// full <c>pg_basebackup</c> base backup is taken and the maximum age a WAL segment may
/// reach before it is forced to the archive (the <c>archive_timeout</c> equivalent). The
/// WAL archive interval should be at or below the recovery point objective so the restorable
/// point never drifts past the objective between base backups (#356).
/// </summary>
public sealed record BackupSchedule
{
    /// <summary>
    /// Initializes a new <see cref="BackupSchedule"/>.
    /// </summary>
    /// <param name="baseBackupInterval">How often a full base backup is taken. Must be greater than zero.</param>
    /// <param name="walArchiveInterval">Maximum age a WAL segment may reach before it is forced to the archive. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either interval is not strictly positive.</exception>
    public BackupSchedule(TimeSpan baseBackupInterval, TimeSpan walArchiveInterval)
    {
        if (baseBackupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseBackupInterval),
                baseBackupInterval,
                "Base backup interval must be greater than zero.");
        }

        if (walArchiveInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(walArchiveInterval),
                walArchiveInterval,
                "WAL archive interval must be greater than zero.");
        }

        BaseBackupInterval = baseBackupInterval;
        WalArchiveInterval = walArchiveInterval;
    }

    /// <summary>
    /// How often a full PostgreSQL base backup is taken.
    /// </summary>
    public TimeSpan BaseBackupInterval { get; }

    /// <summary>
    /// Maximum age a WAL segment may reach before it is forced to the archive.
    /// </summary>
    public TimeSpan WalArchiveInterval { get; }

    /// <summary>
    /// Default enterprise cadence: a daily base backup with a five-minute WAL archive
    /// timeout, aligned with the default five-minute recovery point objective.
    /// </summary>
    public static BackupSchedule Default { get; } =
        new(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Returns true when the WAL archive interval is at or below the recovery point objective,
    /// meaning continuous archiving can keep the restorable point inside the objective. A
    /// schedule that violates this can never sustain the objective even when every backup
    /// succeeds.
    /// </summary>
    public bool SatisfiesRecoveryPointObjective(RecoveryObjectives objectives)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        return WalArchiveInterval <= objectives.RecoveryPointObjective;
    }
}
