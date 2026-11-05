// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

public sealed class LeadershipChangedEventArgs : EventArgs
{
    public bool IsLeader { get; }

    public string? PreviousLeader { get; }

    public string? NewLeader { get; }

    public LeadershipChangedEventArgs(bool isLeader, string? previousLeader, string? newLeader)
    {
        IsLeader = isLeader;
        PreviousLeader = previousLeader;
        NewLeader = newLeader;
    }
}
