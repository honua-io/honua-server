// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Resolves stable deploy targets for workflow operations.
/// </summary>
public interface IDeployTargetRegistry
{
    /// <summary>
    /// Lists deploy targets known to the current control-plane instance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Known deploy targets.</returns>
    Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a deploy target by stable identifier.
    /// </summary>
    /// <param name="targetId">Stable target identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Target definition or null when not found.</returns>
    Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves stable workload definitions for execution jobs.
/// </summary>
public interface IExecutionJobDefinitionRegistry
{
    /// <summary>
    /// Lists execution job definitions known to the current control-plane instance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Known workload definitions.</returns>
    Task<IReadOnlyList<ExecutionJobDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a workload definition by stable identifier.
    /// </summary>
    /// <param name="workloadId">Stable workload identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Workload definition or null when not found.</returns>
    Task<ExecutionJobDefinition?> GetAsync(string workloadId, CancellationToken cancellationToken = default);
}
