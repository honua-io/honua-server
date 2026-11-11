// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

// Keeps branch-version reconcile and post jobs pollable from any replica and resumable after a restart.
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
