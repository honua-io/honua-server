// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Redis;

/// <summary>
/// Interface for Redis-backed distributed leader election with fallback capabilities.
/// </summary>
public interface IRedisLeaderElection : IRedisService
{
    /// <summary>
    /// Gets the unique identifier for this node.
    /// </summary>
    string NodeId { get; }

    /// <summary>
    /// Gets the leadership key used by this election.
    /// </summary>
    string LeadershipKey { get; }

    /// <summary>
    /// Gets a value indicating whether this node is currently the leader.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Gets the current leader's node ID, if any.
    /// </summary>
    string? CurrentLeader { get; }

    /// <summary>
    /// Gets the duration of the leadership lease.
    /// </summary>
    TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Attempts to acquire or extend leadership.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if leadership was acquired or extended, false otherwise</returns>
    Task<bool> TryAcquireOrExtendLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases leadership voluntarily.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Occurs when leadership status changes.
    /// </summary>
    event EventHandler<LeadershipChangedEventArgs>? LeadershipChanged;
}

/// <summary>
/// Event arguments for leadership change events.
/// </summary>
public sealed class LeadershipChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LeadershipChangedEventArgs"/> class.
    /// </summary>
    /// <param name="isLeader">Whether this node is now the leader</param>
    /// <param name="previousLeader">The previous leader node ID</param>
    /// <param name="currentLeader">The current leader node ID</param>
    public LeadershipChangedEventArgs(bool isLeader, string? previousLeader, string? currentLeader)
    {
        IsLeader = isLeader;
        PreviousLeader = previousLeader;
        CurrentLeader = currentLeader;
    }

    /// <summary>
    /// Gets a value indicating whether this node is now the leader.
    /// </summary>
    public bool IsLeader { get; }

    /// <summary>
    /// Gets the previous leader node ID.
    /// </summary>
    public string? PreviousLeader { get; }

    /// <summary>
    /// Gets the current leader node ID.
    /// </summary>
    public string? CurrentLeader { get; }
}