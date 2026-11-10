// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Deployment.Domain;

namespace Honua.Core.Features.Deployment.Abstractions;

public interface IDeploymentStore
{
    Task<bool> TryCreateAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    Task<Domain.Deployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default);

    Task<bool> TrySetAsync(
        Domain.Deployment deployment,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Domain.Deployment>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Domain.Deployment>> ListBySourceAsync(
        DeploymentSourceKind sourceKind,
        string sourceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Domain.Deployment>> ListByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default);
}
