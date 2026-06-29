// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

/// <summary>
/// Configuration that governs an active-passive failover playbook: how many consecutive failed
/// health checks against the primary trigger a failover, and whether the standby must be
/// recoverable inside its objectives before automated failover is allowed to proceed
/// (#356, ADR-0024).
/// </summary>
public sealed record FailoverPolicy
{
    /// <summary>
    /// Initializes a new <see cref="FailoverPolicy"/>.
    /// </summary>
    /// <param name="failureThreshold">
    /// Number of consecutive failed health checks against the primary required before failover
    /// is triggered. Must be greater than zero.
    /// </param>
    /// <param name="requireStandbyRecoveryReady">
    /// When true (the default), automated failover is blocked unless the standby is recoverable
    /// inside its recovery objectives, preventing a failover that would breach the recovery
    /// point objective.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="failureThreshold"/> is not strictly positive.</exception>
    public FailoverPolicy(int failureThreshold, bool requireStandbyRecoveryReady = true)
    {
        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureThreshold),
                failureThreshold,
                "Failure threshold must be greater than zero.");
        }

        FailureThreshold = failureThreshold;
        RequireStandbyRecoveryReady = requireStandbyRecoveryReady;
    }

    /// <summary>
    /// Number of consecutive failed health checks against the primary required before failover
    /// is triggered.
    /// </summary>
    public int FailureThreshold { get; }

    /// <summary>
    /// Whether automated failover is blocked unless the standby is recoverable inside its
    /// recovery objectives.
    /// </summary>
    public bool RequireStandbyRecoveryReady { get; }

    /// <summary>
    /// Default enterprise failover policy: three consecutive failed health checks trigger
    /// failover, and the standby must be recoverable inside its objectives first.
    /// </summary>
    public static FailoverPolicy Default { get; } = new(failureThreshold: 3);
}
