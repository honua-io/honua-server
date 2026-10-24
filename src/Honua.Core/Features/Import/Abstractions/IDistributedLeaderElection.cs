// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Distributed leader election for singleton background processing.
/// </summary>
public interface IDistributedLeaderElection
{
    /// <summary>
    /// Try to acquire leadership. Only one instance can be leader at a time.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if this instance is now the leader</returns>
    Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Extend the leadership lease. Must be called periodically to maintain leadership.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lease was extended, false if leadership was lost</returns>
    Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Release leadership voluntarily.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if this instance currently holds leadership.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Unique identifier for this instance.
    /// </summary>
    string InstanceId { get; }
}
