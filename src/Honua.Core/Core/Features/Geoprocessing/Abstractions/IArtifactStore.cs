// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IArtifactStore
{
    Task<Artifact> CreateAsync(Artifact artifact, CancellationToken cancellationToken = default);

    Task<Artifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> ListByWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    Task<bool> TransitionStateAsync(string artifactId, ArtifactLifecycleState newState, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string artifactId, CancellationToken cancellationToken = default);
}
