// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

public sealed record RecoveryObjectives
{
    public RecoveryObjectives(TimeSpan recoveryTimeObjective, TimeSpan recoveryPointObjective)
    {
        if (recoveryTimeObjective <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryTimeObjective),
                recoveryTimeObjective,
                "Recovery time objective must be greater than zero.");
        }

        if (recoveryPointObjective <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryPointObjective),
                recoveryPointObjective,
                "Recovery point objective must be greater than zero.");
        }

        RecoveryTimeObjective = recoveryTimeObjective;
        RecoveryPointObjective = recoveryPointObjective;
    }

    public TimeSpan RecoveryTimeObjective { get; }

    public TimeSpan RecoveryPointObjective { get; }

    public static RecoveryObjectives Default { get; } =
        new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
}
