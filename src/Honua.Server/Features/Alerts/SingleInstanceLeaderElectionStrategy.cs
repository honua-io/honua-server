// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;

namespace Honua.Alerts;

/// <summary>
/// Local fallback used when alert evaluation runs without a database-backed leader election dependency.
/// This keeps single-instance and test hosts working when no distributed coordinator is configured.
/// </summary>
internal sealed class SingleInstanceLeaderElectionStrategy : ILeaderElectionStrategy
{
    public bool IsLeader { get; private set; }

    public string InstanceId { get; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        IsLeader = true;
        return Task.FromResult(true);
    }

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        IsLeader = false;
        return Task.CompletedTask;
    }
}
