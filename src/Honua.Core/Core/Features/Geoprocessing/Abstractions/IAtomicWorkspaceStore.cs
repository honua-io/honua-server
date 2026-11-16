// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IAtomicWorkspaceStore
{
    Task<Workspace> CreateWithQuotaAsync(Workspace proposal, int? maxWorkspaceCount = null, CancellationToken cancellationToken = default);

    Task<Workspace> GetOrCreateNamedAsync(Workspace proposal, int? maxWorkspaceCount = null, CancellationToken cancellationToken = default);

    Task<Artifact?> AddOrReplaceAsync(Artifact artifact, bool overwrite, CancellationToken cancellationToken = default);

    Task<Artifact> CreateArtifactWithQuotaAsync(Artifact artifact, WorkspaceQuota quota, CancellationToken cancellationToken = default);

    Task<Artifact?> AddOrReplaceWithQuotaAsync(Artifact artifact, bool overwrite, WorkspaceQuota quota, CancellationToken cancellationToken = default);

    Task<Artifact?> PublishAsync(Artifact artifact, bool overwrite, WorkspaceQuota quota,
        Func<CancellationToken, Task<bool>> publishReference, CancellationToken cancellationToken = default);
}
