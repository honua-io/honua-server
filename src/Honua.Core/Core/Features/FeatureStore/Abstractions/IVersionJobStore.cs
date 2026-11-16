// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

// Keeps branch-version reconcile and post jobs pollable from any replica and resumable after a restart.
public interface IVersionJobStore
{
    Task SaveAsync(VersionJob job, CancellationToken cancellationToken = default);

    Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);
}
