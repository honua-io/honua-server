// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// Recovery objectives that bound an active-passive disaster-recovery plan.
/// <para>
/// The recovery time objective (RTO) is the maximum tolerable duration to restore service
/// after a disaster. The recovery point objective (RPO) is the maximum tolerable amount of
/// data loss, expressed as the age of the most recent restorable point. Both are validated
/// recovery contracts that the readiness evaluator measures recorded backups against
/// (#356, ADR-0024).
/// </para>
/// </summary>
public sealed record RecoveryObjectives
{
    /// <summary>
    /// Initializes a new <see cref="RecoveryObjectives"/> with the given objectives.
    /// </summary>
    /// <param name="recoveryTimeObjective">Maximum tolerable time to restore service. Must be greater than zero.</param>
    /// <param name="recoveryPointObjective">Maximum tolerable data-loss window. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either objective is not strictly positive.</exception>
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

    /// <summary>
    /// Maximum tolerable time to restore service after a disaster (RTO).
    /// </summary>
    public TimeSpan RecoveryTimeObjective { get; }

    /// <summary>
    /// Maximum tolerable data-loss window (RPO), measured as the age of the most recent
    /// restorable point.
    /// </summary>
    public TimeSpan RecoveryPointObjective { get; }

    /// <summary>
    /// Default enterprise objectives: a one-hour recovery time objective and a five-minute
    /// recovery point objective, matching continuous WAL archiving with a five-minute archive
    /// timeout.
    /// </summary>
    public static RecoveryObjectives Default { get; } =
        new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));
}
