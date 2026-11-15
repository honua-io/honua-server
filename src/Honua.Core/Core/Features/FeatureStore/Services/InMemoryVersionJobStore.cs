// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Services;

// For a host with no system database, which runs one node.
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
