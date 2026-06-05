// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;

namespace Honua.Ai.WorkflowGeneration.Models;

/// <summary>
/// Structured-output shape the model returns. This mirrors the constrained JSON schema sent
/// to the provider: a status, an optional graph (the same <see cref="WorkflowGraph"/> contract
/// the package PUT accepts), a rationale, clarifications, unmapped requests, and an optional
/// capability state. The server projects this into a <see cref="WorkflowGenerationProposal"/>
/// and runs the hard validation gate before returning anything to the client.
/// </summary>
internal sealed record WorkflowGenerationModelProposal
{
    /// <summary>Status string: generated | needs-clarification | unsupported | refused.</summary>
    public string Status { get; init; } = "error";

    /// <summary>Proposed graph (present when status is generated).</summary>
    public WorkflowGraph? Graph { get; init; }

    /// <summary>Human-readable rationale.</summary>
    public string? Rationale { get; init; }

    /// <summary>Clarifications (present when status is needs-clarification).</summary>
    public IReadOnlyList<WorkflowGenerationClarification> Clarifications { get; init; } = [];

    /// <summary>Requested steps with no matching node type.</summary>
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    /// <summary>Optional capability state for a degraded/unsupported step.</summary>
    public WorkflowGenerationCapabilityState? CapabilityState { get; init; }
}
