// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Provider that contributes operation descriptors to the grounding catalog. Mirrors
/// <c>IWorkflowNodeProvider</c>; the catalog aggregates every registered provider. This is
/// the grounding surface the AI will later see. DevOps descriptors join here in a later
/// phase (descriptors only — DevOps execution stays in the agent).
/// </summary>
public interface IOperationDescriptorProvider
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Returns the operation descriptors contributed by this provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregates every registered <see cref="IOperationDescriptorProvider"/> into a single
/// grounding catalog. Mirrors <c>WorkflowNodeRegistry</c>: it produces a versioned snapshot
/// and resolves a single descriptor by id.
/// </summary>
public interface IOperationCatalog
{
    /// <summary>
    /// Builds the current catalog snapshot of all available operation descriptors.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a descriptor by operation id, or null when not found.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationDescriptor?> GetDescriptorAsync(string operationId, CancellationToken cancellationToken = default);
}
