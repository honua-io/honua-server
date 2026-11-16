// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IVersionJobRunner
{
    Task<VersionJob> StartReconcileAsync(
        string service,
        Guid versionId,
        VersionReconcilePolicy policy,
        VersionConflictDetection detection = VersionConflictDetection.ByAttribute,
        CancellationToken cancellationToken = default);

    Task<VersionJob> StartPostAsync(
        string service,
        Guid versionId,
        CancellationToken cancellationToken = default);

    Task<VersionJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
