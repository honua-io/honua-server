// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Summary view of an operation proposal for the console list surface (#1694).
/// </summary>
public sealed record ProposalSummaryResponse
{
    /// <summary>Stable proposal identifier.</summary>
    public required string ProposalId { get; init; }

    /// <summary>Operation class.</summary>
    public required string Kind { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required string Status { get; init; }

    /// <summary>Requesting operator or service principal.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Requesting agent identifier when applicable.</summary>
    public string? RequestedByAgent { get; init; }

    /// <summary>Finding identifier when the proposal originated from deterministic ops findings.</summary>
    public string? FindingId { get; init; }

    /// <summary>Finding rule when the proposal originated from deterministic ops findings.</summary>
    public string? AutonomyRule { get; init; }

    /// <summary>Registered action discriminator without its hidden execution payload.</summary>
    public string? ActionDiscriminator { get; init; }

    /// <summary>Single-line summary of the proposed change.</summary>
    public required string Summary { get; init; }

    /// <summary>Assessed risk level.</summary>
    public required string RiskLevel { get; init; }

    /// <summary>Creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Full detail view of an operation proposal including plan/diff/dry-run/risk (#1694).
/// </summary>
public sealed record ProposalDetailResponse
{
    /// <summary>Stable proposal identifier.</summary>
    public required string ProposalId { get; init; }

    /// <summary>Operation class.</summary>
    public required string Kind { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required string Status { get; init; }

    /// <summary>Requesting operator or service principal.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>Requesting agent identifier when applicable.</summary>
    public string? RequestedByAgent { get; init; }

    /// <summary>Finding identifier when the proposal originated from deterministic ops findings.</summary>
    public string? FindingId { get; init; }

    /// <summary>Finding rule when the proposal originated from deterministic ops findings.</summary>
    public string? AutonomyRule { get; init; }

    /// <summary>Registered action discriminator without its hidden execution payload.</summary>
    public string? ActionDiscriminator { get; init; }

    /// <summary>Single-line summary of the proposed change.</summary>
    public required string Summary { get; init; }

    /// <summary>Structured diff lines.</summary>
    public required IReadOnlyList<string> Diff { get; init; }

    /// <summary>Dry-run output lines.</summary>
    public required IReadOnlyList<string> DryRun { get; init; }

    /// <summary>Assessed risk level.</summary>
    public required string RiskLevel { get; init; }

    /// <summary>Reasons blocking application.</summary>
    public required IReadOnlyList<string> BlockingReasons { get; init; }

    /// <summary>Non-blocking advisories.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Guardrail tier that routed this to approval.</summary>
    public string? GuardrailTier { get; init; }

    /// <summary>Principal that resolved the proposal.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>Resolution reason.</summary>
    public string? ResolutionReason { get; init; }

    /// <summary>Underlying execution operation id when applied.</summary>
    public string? ExecutionOperationId { get; init; }

    /// <summary>Creation timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Resolution timestamp.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>List wrapper for proposal summaries.</summary>
public sealed record ProposalListResponse
{
    /// <summary>Returned proposals.</summary>
    public required IReadOnlyList<ProposalSummaryResponse> Proposals { get; init; }
}

/// <summary>Reject request body — reason is required (#1694).</summary>
public sealed record RejectProposalRequest
{
    /// <summary>Required rejection reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>Source-generated context for proposal admin DTOs.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProposalSummaryResponse))]
[JsonSerializable(typeof(ProposalDetailResponse))]
[JsonSerializable(typeof(ProposalListResponse))]
[JsonSerializable(typeof(RejectProposalRequest))]
public sealed partial class ProposalJsonContext : JsonSerializerContext;
