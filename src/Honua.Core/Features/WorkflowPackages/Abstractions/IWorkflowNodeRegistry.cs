// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Abstractions;

/// <summary>
/// Read-only registry of server-owned workflow node definitions.
/// </summary>
public interface IWorkflowNodeRegistry
{
    /// <summary>
    /// Builds the current node registry snapshot.
    /// </summary>
    Task<WorkflowNodeRegistrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a node definition by node type identifier.
    /// </summary>
    Task<WorkflowNodeDefinition?> GetNodeAsync(string nodeTypeId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider that contributes node definitions to the workflow node registry.
/// </summary>
public interface IWorkflowNodeProvider
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Provider version included in registry fingerprints.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Returns node definitions contributed by the provider.
    /// </summary>
    Task<IReadOnlyList<WorkflowNodeDefinition>> ListNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns provider-level warnings for the current server state.
    /// </summary>
    Task<IReadOnlyList<string>> GetWarningsAsync(CancellationToken cancellationToken = default);
}
