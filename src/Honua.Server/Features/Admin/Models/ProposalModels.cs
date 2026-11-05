// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

public sealed record ProposalSummaryResponse
{
    public required string ProposalId { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public string? RequestedBy { get; init; }

    public string? RequestedByAgent { get; init; }

    public string? FindingId { get; init; }

    public string? AutonomyRule { get; init; }

    public string? ActionDiscriminator { get; init; }

    public required string Summary { get; init; }

    public required string RiskLevel { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProposalDetailResponse
{
    public required string ProposalId { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public string? RequestedBy { get; init; }

    public string? RequestedByAgent { get; init; }

    public string? FindingId { get; init; }

    public string? AutonomyRule { get; init; }

    public string? ActionDiscriminator { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<string> Diff { get; init; }

    public required IReadOnlyList<string> DryRun { get; init; }

    public required string RiskLevel { get; init; }

    public required IReadOnlyList<string> BlockingReasons { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public string? GuardrailTier { get; init; }

    public string? ResolvedBy { get; init; }

    public string? ResolutionReason { get; init; }

    public string? ExecutionOperationId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }
}

public sealed record ProposalListResponse
{
    public required IReadOnlyList<ProposalSummaryResponse> Proposals { get; init; }
}

public sealed record RejectProposalRequest
{
    public string? Reason { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProposalSummaryResponse))]
[JsonSerializable(typeof(ProposalDetailResponse))]
[JsonSerializable(typeof(ProposalListResponse))]
[JsonSerializable(typeof(RejectProposalRequest))]
public sealed partial class ProposalJsonContext : JsonSerializerContext;
