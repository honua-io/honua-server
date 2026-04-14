// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Event arguments for leadership status changes in distributed leader election.
/// </summary>
public sealed class LeadershipChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets a value indicating whether this node is now the leader.
    /// </summary>
    public bool IsLeader { get; }

    /// <summary>
    /// Gets the ID of the previous leader, if any.
    /// </summary>
    public string? PreviousLeader { get; }

    /// <summary>
    /// Gets the ID of the new leader, if any.
    /// </summary>
    public string? NewLeader { get; }

    /// <summary>
    /// Initializes a new instance of the LeadershipChangedEventArgs class.
    /// </summary>
    /// <param name="isLeader">Whether this node is now the leader</param>
    /// <param name="previousLeader">The ID of the previous leader</param>
    /// <param name="newLeader">The ID of the new leader</param>
    public LeadershipChangedEventArgs(bool isLeader, string? previousLeader, string? newLeader)
    {
        IsLeader = isLeader;
        PreviousLeader = previousLeader;
        NewLeader = newLeader;
    }
}
