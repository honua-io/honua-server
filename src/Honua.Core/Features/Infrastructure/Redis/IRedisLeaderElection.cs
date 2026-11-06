// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Redis;

public interface IRedisLeaderElection : IRedisService
{
    string NodeId { get; }

    string LeadershipKey { get; }

    bool IsLeader { get; }

    string? CurrentLeader { get; }

    TimeSpan LeaseDuration { get; }

    Task<bool> TryAcquireOrExtendLeadershipAsync(CancellationToken cancellationToken = default);

    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);

    event EventHandler<LeadershipChangedEventArgs>? LeadershipChanged;
}

public sealed class LeadershipChangedEventArgs : EventArgs
{
    public LeadershipChangedEventArgs(bool isLeader, string? previousLeader, string? currentLeader)
    {
        IsLeader = isLeader;
        PreviousLeader = previousLeader;
        CurrentLeader = currentLeader;
    }

    public bool IsLeader { get; }

    public string? PreviousLeader { get; }

    public string? CurrentLeader { get; }
}
