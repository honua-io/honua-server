// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Operations.Domain;

public sealed record OperationRequest
{
    [JsonIgnore]
    public VerifiedCommittedSinkPublication? VerifiedCommittedSinkPublication { get; init; }
    public required string OperationId { get; init; }

    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public string? ConnectionId { get; init; }

    public string? ServiceName { get; init; }

    public IReadOnlyList<string> Fields { get; init; } = [];

    public bool DryRun { get; init; }

    public Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest? GatewayRequest { get; init; }

    // Approval-payload secret references are resolved only by the approved REST executor and are never part of the public request.
    public IReadOnlyDictionary<string, OperationSecretReference> SecretParameters { get; init; } =
        new Dictionary<string, OperationSecretReference>(StringComparer.Ordinal);
}

public sealed record VerifiedCommittedSinkPublication
{
    public required string SourceJobId { get; init; }

    public required string SourceArtifactId { get; init; }

    public required Guid ConnectionId { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string EndpointFingerprint { get; init; }

    public required string SourceOwnerActorId { get; init; }

    public required string PublisherActorId { get; init; }

    public bool PublisherWasAdministrator { get; init; }

    public string? TenantId { get; init; }

    public const string SourceJobIdKey = "committedSinkSourceJobId";

    public const string SourceArtifactIdKey = "committedSinkSourceArtifactId";

    public const string PublisherActorIdKey = "committedSinkPublisherActorId";

    public const string PublisherWasAdministratorKey = "committedSinkPublisherWasAdministrator";
}

public sealed record OperationSecretReference
{
    public required string ReferenceId { get; init; }

    public required string Name { get; init; }
}

public sealed record OperationPolicyContext
{
    // Protocol adapters must never set the proposal identity; only the canonical proposal runtime does.
    public string? ApprovedProposalId { get; init; }

    public string? ApprovedPlanHash { get; init; }

    // Protocol adapters must not derive the invocation identity from the descriptor id or a resource id.
    public string? OperationInstanceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public string? TenantId { get; init; }

    public string? SchemaName { get; init; }

    public string? IdempotencyKey { get; init; }

    // The idempotency digest is scoped to the authenticated principal and tenant and must not contain credentials or unredacted request data.
    public string? IdempotencyRequestHash { get; init; }

    public string? AuthorizationOutcome { get; init; }

    public string? ResolvedConnectionString { get; init; }

    public string? PrincipalId { get; init; }

    public string? Tier { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public bool ScopeGoverned { get; init; }

    public IReadOnlyList<string> RecognizedScopes { get; init; } = [];

    public string? RootProvisioningOperationId { get; init; }
}

public sealed record OperationValidation
{
    public required bool IsValid { get; init; }

    public required string Status { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];

    public string? ErrorKind { get; init; }

    public Honua.Core.Features.ControlPlane.Domain.OperationProposalPlan? ApprovalPlan { get; init; }
}

public sealed record OperationHandle
{
    public string? TenantId { get; init; }

    public long Version { get; init; }

    public required string OperationInstanceId { get; init; }

    public required string OperationId { get; init; }

    public required OperationHandleStatus Status { get; init; }

    // This legacy alias cannot receive a proposal id or any other downstream identity.
    public string HandleId => OperationInstanceId;

    public required string CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? AuthorizationOutcome { get; init; }

    public PolicyDecisionKind? PolicyDecision { get; init; }

    public OperationResultSummary? Result { get; init; }

    public string? JobId { get; init; }

    public string? ApprovalLane { get; init; }

    public long? MetadataRevision { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, string> ResourceIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    public string? RootProvisioningOperationId { get; init; }
}

public sealed record OperationResultSummary
{
    public required string Summary { get; init; }

    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Secret output material is available only through the authenticated consume-once channel and is never part of the durable result.
    public IReadOnlyList<OperationSecretReference> SecretReferences { get; init; } = [];
}

public sealed record OperationStatus
{
    public required string OperationInstanceId { get; init; }

    public required string OperationId { get; init; }

    public string HandleId => OperationInstanceId;

    public required string CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? AuthorizationOutcome { get; init; }

    public PolicyDecisionKind? PolicyDecision { get; init; }

    public required OperationHandleStatus Status { get; init; }

    public OperationResultSummary? Result { get; init; }

    public string? JobId { get; init; }

    public string? ApprovalLane { get; init; }

    public long? MetadataRevision { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, string> ResourceIds { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    // A status read does not invent a provisioning root the handle does not carry.
    public string? RootProvisioningOperationId { get; init; }
}

public enum PolicyDecisionKind
{
    Allow,

    RequireApproval,

    DryRunFirst,

    Deny
}

public sealed record PolicyDecision
{
    public required PolicyDecisionKind Kind { get; init; }

    public string? Reason { get; init; }

    public string? ApprovalLane { get; init; }

    public static PolicyDecision Allowed { get; } = new() { Kind = PolicyDecisionKind.Allow };
}
