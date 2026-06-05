// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Generation.Domain;

/// <summary>
/// Request handed to an <see cref="Abstractions.IWorkflowGenerationProvider"/>. Carries the
/// prompt, prior turns, answered clarifications, the current graph (refine turns), and the
/// grounded node whitelist the provider must constrain its output to.
/// </summary>
public sealed record WorkflowGenerationProviderRequest
{
    /// <summary>The natural-language prompt for this turn.</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional per-call model override.</summary>
    public string? ModelOverride { get; init; }

    /// <summary>Prior conversation turns for context.</summary>
    public IReadOnlyList<WorkflowGenerationConversationTurn> Conversation { get; init; } = [];

    /// <summary>Answers to a prior clarification turn.</summary>
    public IReadOnlyList<WorkflowGenerationAnswer> Answers { get; init; } = [];

    /// <summary>The current graph when refining an existing draft; null for fresh generation.</summary>
    public WorkflowGraph? CurrentGraph { get; init; }

    /// <summary>
    /// The grounded node-registry snapshot. Providers MUST constrain proposed node types
    /// to <see cref="WorkflowNodeRegistrySnapshot.Nodes"/>.
    /// </summary>
    public required WorkflowNodeRegistrySnapshot Registry { get; init; }

    /// <summary>Validator failures fed back during a bounded repair re-prompt.</summary>
    public IReadOnlyList<WorkflowPackageValidationFailure> RepairFailures { get; init; } = [];
}

/// <summary>
/// Raw proposal returned by a provider before the server runs the hard validation gate.
/// </summary>
public sealed record WorkflowGenerationProposal
{
    /// <summary>The provider-chosen outcome for the turn.</summary>
    public required WorkflowGenerationStatus Status { get; init; }

    /// <summary>The proposed graph; present when <see cref="Status"/> is generated.</summary>
    public WorkflowGraph? Graph { get; init; }

    /// <summary>Human-readable rationale.</summary>
    public string? Rationale { get; init; }

    /// <summary>Clarifications when the provider needs more information.</summary>
    public IReadOnlyList<WorkflowGenerationClarification> Clarifications { get; init; } = [];

    /// <summary>Requested steps the provider could not map to a node type.</summary>
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    /// <summary>Optional capability state when a step was degraded or unsupported.</summary>
    public WorkflowGenerationCapabilityState? CapabilityState { get; init; }

    /// <summary>Provider id that produced the proposal.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Model id that produced the proposal.</summary>
    public string? Model { get; init; }

    /// <summary>Usage and latency, when reported.</summary>
    public WorkflowGenerationUsage? Usage { get; init; }

    /// <summary>An error message when <see cref="Status"/> is error.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a refused proposal.</summary>
    public static WorkflowGenerationProposal Refused(string reason, string? providerId = null, string? model = null)
        => new() { Status = WorkflowGenerationStatus.Refused, Rationale = reason, ProviderId = providerId, Model = model };

    /// <summary>Creates an error proposal.</summary>
    public static WorkflowGenerationProposal Error(string message, string? providerId = null, string? model = null)
        => new() { Status = WorkflowGenerationStatus.Error, ErrorMessage = message, ProviderId = providerId, Model = model };
}
