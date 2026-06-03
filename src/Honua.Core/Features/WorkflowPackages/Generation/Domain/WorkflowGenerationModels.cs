// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Generation.Domain;

/// <summary>
/// Status of a natural-language workflow generation turn. Serialized as kebab-case
/// string literals over the wire (the console matches "generated", "needs-clarification", etc.).
/// </summary>
public enum WorkflowGenerationStatus
{
    /// <summary>A validated graph was produced.</summary>
    [JsonStringEnumMemberName("generated")]
    Generated,

    /// <summary>The request is ambiguous; structured clarifications are returned.</summary>
    [JsonStringEnumMemberName("needs-clarification")]
    NeedsClarification,

    /// <summary>A requested capability is not available on this server.</summary>
    [JsonStringEnumMemberName("unsupported")]
    Unsupported,

    /// <summary>The provider refused the request (for example, off-topic).</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,

    /// <summary>A transport or provider error occurred; the turn is retryable.</summary>
    [JsonStringEnumMemberName("error")]
    Error
}

/// <summary>
/// One prior conversation turn supplied for context on a follow-up generation request.
/// </summary>
public sealed record WorkflowGenerationConversationTurn
{
    /// <summary>Author role: <c>user</c> or <c>assistant</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Turn content.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// Answer to a prior <see cref="WorkflowGenerationStatus.NeedsClarification"/> turn.
/// </summary>
public sealed record WorkflowGenerationAnswer
{
    /// <summary>Identifier of the clarification question being answered.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Identifier of the chosen option.</summary>
    public required string OptionId { get; init; }
}

/// <summary>
/// A structured clarification question surfaced to the console (flattened projection of
/// the MCP clarification envelope).
/// </summary>
public sealed record WorkflowGenerationClarification
{
    /// <summary>Stable question identifier echoed back in answers.</summary>
    public required string Id { get; init; }

    /// <summary>Clarification kind, for example <c>source</c>, <c>schedule</c>, or <c>target</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Human-readable question prompt.</summary>
    public required string Prompt { get; init; }

    /// <summary>Why the clarification is required.</summary>
    public string? Reason { get; init; }

    /// <summary>Selectable choices.</summary>
    public IReadOnlyList<WorkflowGenerationClarificationChoice> Choices { get; init; } = [];
}

/// <summary>
/// A single selectable clarification choice.
/// </summary>
public sealed record WorkflowGenerationClarificationChoice
{
    /// <summary>Stable option identifier echoed back in answers.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable option label.</summary>
    public required string Label { get; init; }

    /// <summary>Optional human-readable effect of selecting this option.</summary>
    public string? Effect { get; init; }
}

/// <summary>
/// Capability state for a requested step, reusing the AI-builder capability literals
/// (<c>supported</c>, <c>degraded</c>, <c>unsupported</c>, <c>auth_denied</c>, <c>oversized</c>).
/// </summary>
public sealed record WorkflowGenerationCapabilityState
{
    /// <summary>The capability or step name.</summary>
    public required string Name { get; init; }

    /// <summary>Capability state literal.</summary>
    public required string State { get; init; }

    /// <summary>Optional reason explaining the state.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Token usage and latency for a generation turn.
/// </summary>
public sealed record WorkflowGenerationUsage
{
    /// <summary>Prompt tokens consumed, when reported by the provider.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens consumed, when reported by the provider.</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>End-to-end provider latency in milliseconds.</summary>
    public long LatencyMs { get; init; }
}

/// <summary>
/// Result of a natural-language workflow generation turn. This is the <c>data</c>
/// payload returned by <c>POST /api/v1/console/workflow-packages/generate</c>.
/// </summary>
public sealed record WorkflowGenerationResult
{
    /// <summary>Turn status.</summary>
    public required WorkflowGenerationStatus Status { get; init; }

    /// <summary>
    /// The validated workflow graph, present iff <see cref="Status"/> is
    /// <see cref="WorkflowGenerationStatus.Generated"/>.
    /// </summary>
    public WorkflowGraph? Graph { get; init; }

    /// <summary>Human-readable rationale for the proposed graph or status.</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// Clarification questions, present iff <see cref="Status"/> is
    /// <see cref="WorkflowGenerationStatus.NeedsClarification"/>.
    /// </summary>
    public IReadOnlyList<WorkflowGenerationClarification> Clarifications { get; init; } = [];

    /// <summary>Validator result attached to every turn that produced a graph.</summary>
    public WorkflowPackageValidationResult? Validation { get; init; }

    /// <summary>Requested steps that had no matching node type.</summary>
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    /// <summary>Capability state when a requested step was degraded or unavailable.</summary>
    public WorkflowGenerationCapabilityState? CapabilityState { get; init; }

    /// <summary>Provider id that produced the turn.</summary>
    public string? Provider { get; init; }

    /// <summary>Model id that produced the turn.</summary>
    public string? Model { get; init; }

    /// <summary>Node-registry version the graph was grounded against.</summary>
    public string? RegistryVersion { get; init; }

    /// <summary>Usage and latency, when available.</summary>
    public WorkflowGenerationUsage? Usage { get; init; }
}

/// <summary>
/// Provider descriptor surfaced by the providers discovery endpoint.
/// </summary>
public sealed record WorkflowGenerationProviderDescriptor
{
    /// <summary>Stable provider id: <c>local</c>, <c>anthropic</c>, <c>openai</c>, or <c>deterministic</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label.</summary>
    public required string Label { get; init; }

    /// <summary>Provider kind, equal to the id for the built-in providers.</summary>
    public required string Kind { get; init; }

    /// <summary>Whether the provider is enabled and fully configured.</summary>
    public required bool Available { get; init; }

    /// <summary>Human-readable detail (model + host, or the reason it is unavailable).</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Response payload for <c>GET /api/v1/console/workflow-generation/providers</c>.
/// </summary>
public sealed record WorkflowGenerationProviders
{
    /// <summary>Whether the generation feature is enabled and has at least one usable provider.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Server default provider id.</summary>
    public string? DefaultProvider { get; init; }

    /// <summary>Provider descriptors.</summary>
    public IReadOnlyList<WorkflowGenerationProviderDescriptor> Providers { get; init; } = [];
}
