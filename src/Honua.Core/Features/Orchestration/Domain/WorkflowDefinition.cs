// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Declarative specification of a workflow DAG composed of canonical analysis-plan steps.
/// </summary>
public sealed record WorkflowDefinition
{
    /// <summary>
    /// Stable workflow identifier used by the orchestration APIs and stores.
    /// </summary>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Human-readable workflow name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional free-form description surfaced in admin tooling.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Steps that compose the workflow DAG. Must be non-empty and free of cycles.
    /// </summary>
    public required IReadOnlyList<WorkflowStepDefinition> Steps { get; init; }

    /// <summary>
    /// Optional trigger declaration. Null indicates a manual-only workflow.
    /// </summary>
    public WorkflowTrigger? Trigger { get; init; }

    /// <summary>
    /// Time when the workflow definition was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Time when the workflow definition was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Opaque free-form metadata attached to the definition.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
