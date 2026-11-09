// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

public sealed record BackupSchedule
{
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

    public TimeSpan BaseBackupInterval { get; }

    public TimeSpan WalArchiveInterval { get; }

    public static BackupSchedule Default { get; } =
        new(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5));

    public bool SatisfiesRecoveryPointObjective(RecoveryObjectives objectives)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        return WalArchiveInterval <= objectives.RecoveryPointObjective;
    }
}
