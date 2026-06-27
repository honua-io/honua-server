// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// An immutable record of a single backup attempt produced by the backup automation.
/// Records are the evidence the recovery-readiness evaluator consumes to compute the
/// restorable point and recovery-point compliance (#356).
/// </summary>
/// <param name="Id">Stable identifier for the backup artifact.</param>
/// <param name="Kind">Category of the backup artifact.</param>
/// <param name="CompletedAt">When the backup attempt finished.</param>
/// <param name="Succeeded">Whether the backup completed successfully and is restorable.</param>
/// <param name="SizeBytes">Size of the backup artifact in bytes; null when unknown or failed.</param>
/// <param name="FailureReason">Operator-facing failure reason when <paramref name="Succeeded"/> is false.</param>
public sealed record BackupRecord(
    string Id,
    BackupKind Kind,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    long? SizeBytes = null,
    string? FailureReason = null)
{
    /// <summary>
    /// Returns true when this record represents a PostgreSQL artifact that protects
    /// committed data — a base backup or an archived WAL segment. Redis snapshots warm a
    /// cache and do not extend the restorable point, so they are excluded.
    /// </summary>
    public bool IsDataProtecting
        => Kind is BackupKind.PostgresBase or BackupKind.PostgresWal;
}
