// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

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

    /// <summary>
    /// Row/field security identity of the principal that PUBLISHED this workflow, captured at
    /// publication time.
    /// </summary>
    /// <remarks>
    /// A cron or event-triggered run has no requesting principal — the scheduler and event
    /// services create it under the synthesized orchestrator identity, which carries
    /// <c>role=admin</c>. Capturing the run's snapshot from that principal would give every
    /// step job ADMIN row/field visibility, so a restricted author's scheduled workflow would
    /// read all rows and unmasked fields; the publication-time authorization check only
    /// verifies layer access and does not preserve the author's RLS claims or field-mask roles.
    /// This snapshot is what those runs inherit instead (honua-server#3068 review).
    /// <para>
    /// <see langword="null"/> means the definition predates this field. Triggered runs are
    /// REFUSED in that case rather than falling back to the orchestrator capture, on the same
    /// fail-closed reasoning as the job submit path; the workflow must be republished.
    /// </para>
    /// </remarks>
    public JobSecurityContext? AuthorSecurityContext { get; init; }
}
