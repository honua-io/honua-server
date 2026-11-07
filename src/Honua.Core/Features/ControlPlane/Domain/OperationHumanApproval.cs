// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum OperationInitiationKind
{
    Unknown,
    Human,
    Agent
}

public enum OperationInitiationChannel
{
    Unknown,
    InteractiveOidc,
    Mcp,
    NoninteractiveHttp,
    InternalAgent
}

public sealed record OperationInitiationEvidence
{
    public const int CurrentVersion = 1;

    public int Version { get; init; }
    public OperationInitiationKind Kind { get; init; }
    public OperationInitiationChannel Channel { get; init; }
    public string? PrincipalId { get; init; }
    public string? TenantId { get; init; }
    public required string PlanHash { get; init; }
    public required string PolicyRevision { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
}

public sealed record OperationHumanApprovalEvidence
{
    public const int CurrentVersion = 1;
    public const string InteractiveOidcChannel = "console-interactive-oidc";

    public int Version { get; init; }
    public required string ActorId { get; init; }
    public required string Channel { get; init; }
    public required string SessionReference { get; init; }
    public required string CapabilityReference { get; init; }
    public required string PlanHash { get; init; }
    public required string InitiationDigest { get; init; }
    public required long ProposalVersion { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
}
