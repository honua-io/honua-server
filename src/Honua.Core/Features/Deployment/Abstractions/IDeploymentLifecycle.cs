// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Deployment.Domain;

namespace Honua.Core.Features.Deployment.Abstractions;

/// <summary>
/// Store for persisting and querying deployment lifecycle records.
/// </summary>
public interface IDeploymentStore
{
    /// <summary>
    /// Attempts to create a new deployment record.
    /// </summary>
    /// <returns>True when created; false when a deployment with the same ID already exists.</returns>
    Task<bool> TryCreateAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a deployment record by identifier.
    /// </summary>
    Task<Domain.Deployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest deployment record state, including any appended transitions.
    /// </summary>
    Task SetAsync(
        Domain.Deployment deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deployments whose status is not <see cref="DeploymentStatus.Retired"/> or
    /// <see cref="DeploymentStatus.Superseded"/>.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deployments backed by a specific promoted artifact.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListBySourceAsync(
        DeploymentSourceKind sourceKind,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists deployments mapped to a specific hosting target.
    /// </summary>
    Task<IReadOnlyList<Domain.Deployment>> ListByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default);
}
