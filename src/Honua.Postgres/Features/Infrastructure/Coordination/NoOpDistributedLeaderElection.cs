// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;

namespace Honua.Postgres.Features.Infrastructure.Coordination;

/// <summary>
/// No-op implementation of distributed leader election that always assumes leadership.
/// Used as a fallback when Redis is not available or for single-instance deployments.
/// </summary>
internal sealed class NoOpDistributedLeaderElection : IDistributedLeaderElection
{
    private readonly string _instanceId;

    public NoOpDistributedLeaderElection(string key)
    {
        _instanceId = Environment.MachineName + "_" + Environment.ProcessId;
    }

    /// <inheritdoc />
    public bool IsLeader => true;

    /// <inheritdoc />
    public string InstanceId => _instanceId;

    /// <inheritdoc />
    public Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <inheritdoc />
    public Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}