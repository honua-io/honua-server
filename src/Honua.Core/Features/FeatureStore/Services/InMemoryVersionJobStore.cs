// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Single-node fallback <see cref="IVersionJobStore"/> (#1553) used when Redis is not configured. Keeps
/// reconcile/post job records in process memory so the async job surface is pollable within one node.
/// The Redis-backed implementation supersedes it for multi-replica and restart-durable deployments.
/// </summary>
public sealed class InMemoryVersionJobStore : IVersionJobStore
{
    private readonly ConcurrentDictionary<Guid, VersionJob> _jobs = new();

    /// <inheritdoc />
    public Task SaveAsync(VersionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs[job.JobId] = job;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
        => Task.FromResult(_jobs.TryGetValue(jobId, out var job) ? job : null);
}
