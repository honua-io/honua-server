// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Durable store for declarative workflow definitions.
/// </summary>
public interface IWorkflowDefinitionStore
{
    /// <summary>
    /// Retrieves a workflow definition by identifier.
    /// </summary>
    Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to atomically create a workflow definition.
    /// </summary>
    /// <returns>True when created; false when a definition with the same identifier already exists.</returns>
    Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces the workflow definition with the given identifier.
    /// </summary>
    Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all stored workflow definitions.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns workflow definitions whose trigger is cron-based and enabled.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the workflow definition with the given identifier.
    /// </summary>
    /// <returns>True when a definition was removed.</returns>
    Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default);
}
