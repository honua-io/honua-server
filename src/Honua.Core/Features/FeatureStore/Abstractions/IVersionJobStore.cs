// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Durable store for asynchronous branch-version reconcile/post jobs (#1553). Persists each job's
/// lifecycle state so the job is pollable across requests and across replicas, and so a worker can
/// resume/idempotently re-run a job that was interrupted by a restart. The production implementation is
/// Redis-backed; a single-node in-memory implementation is the fallback when Redis is not configured.
/// </summary>
public interface IVersionJobStore
{
    /// <summary>Inserts or replaces a job record.</summary>
    /// <param name="job">Job to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(VersionJob job, CancellationToken cancellationToken = default);

    /// <summary>Loads a job record by id, or null when it is unknown or has expired.</summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The job record, or null.</returns>
    Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);
}
