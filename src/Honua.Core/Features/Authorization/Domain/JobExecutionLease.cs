// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>A server-issued authority sealed to one job; never an external request bearer credential.</summary>
public sealed record JobExecutionLease
{
    /// <summary>Server-generated lease identity.</summary>
    public required string LeaseId { get; init; }
    /// <summary>Exact execution job identity.</summary>
    public required string JobId { get; init; }
    /// <summary>Server issuance time.</summary>
    public required DateTimeOffset IssuedAt { get; init; }
    /// <summary>Hard execution deadline; not inferred from a refreshed token.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Purpose-bound server protection of the complete immutable authority binding.</summary>
    public required string ProtectedAuthority { get; init; }
}

/// <summary>Exact canonical resource/action reference inside a lease; write never implicitly confers read.</summary>
public sealed record JobExecutionLeaseResource
{
    /// <summary>Canonical operator resource category.</summary>
    public required OperatorResourceType ResourceType { get; init; }
    /// <summary>Stable canonical resource identity.</summary>
    public required string ResourceId { get; init; }
    /// <summary>Exact shared resource operation.</summary>
    public required AuthorizationOperation Operation { get; init; }
    /// <summary>Canonical storage-layer index when this is a catalog layer.</summary>
    public int? StorageLayerId { get; init; }
}

/// <summary>Kind-specific, server-derived closed resource and side-effect scope.</summary>
public sealed record JobExecutionLeaseScope
{
    /// <summary>Job kind that owns the scope adapter.</summary>
    public required ExecutionJobKind Kind { get; init; }
    /// <summary>Exact resource actions; an empty list must be explicitly established by the adapter.</summary>
    public required JobExecutionLeaseResource[] Resources { get; init; }
    /// <summary>Canonical operation requests needed for this immutable intent, including side effects.</summary>
    public required OperatorAuthorizationRequest[] Operations { get; init; }
    /// <summary>Bounded, server-derived provider identity/content/policy binding; never caller grant metadata.</summary>
    public required string ProviderBinding { get; init; }
}

/// <summary>Task acceptance authority sealed by the existing workspace CAS before job creation.</summary>
public sealed record JobExecutionLeaseAdmissionBinding
{
    /// <summary>Original workspace identity.</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>Original immutable task identity.</summary>
    public required string TaskId { get; init; }
    /// <summary>Original immutable task revision.</summary>
    public required long TaskRevision { get; init; }
    /// <summary>Original immutable task content hash.</summary>
    public required string TaskContentHash { get; init; }
    /// <summary>Exact prepared authority revision; later task renewal cannot replace it.</summary>
    public required long AuthorityRevision { get; init; }
    /// <summary>Exact prepared executor snapshot fingerprint, including its original expiry.</summary>
    public required string AuthorityFingerprint { get; init; }
    /// <summary>Immutable original policy-context fingerprint.</summary>
    public required string PolicyContextFingerprint { get; init; }
    /// <summary>Initiating human actor when the task originated with one.</summary>
    public string? InitiatingHumanActorId { get; init; }
    /// <summary>Original turn identity.</summary>
    public required string OriginatingTurnId { get; init; }
    /// <summary>Original turn content hash, distinct from task content.</summary>
    public required string OriginalTurnHash { get; init; }
}

/// <summary>Live server issuance input; not itself proof of an approval or authority.</summary>
public sealed record JobExecutionLeaseIssuanceRequest
{
    /// <summary>The exact prepared, immutable job before durable acceptance.</summary>
    public required ExecutionJobRecord PreparedJob { get; init; }
    /// <summary>Actual fresh framework-authenticated executor, never serialized.</summary>
    [JsonIgnore]
    public required ClaimsPrincipal Executor { get; init; }
    /// <summary>Requested finite deadline, bounded again by host, job and canonical policy.</summary>
    public required DateTimeOffset RequestedExpiresAt { get; init; }
    /// <summary>Prepared task CAS binding when the job originates from a workspace task.</summary>
    public JobExecutionLeaseAdmissionBinding? Admission { get; init; }
}

/// <summary>Canonical side-effect/approval decision; benign operations need no extra human approval.</summary>
public sealed record JobExecutionLeasePolicyDecision
{
    /// <summary>Whether canonical operation and approval policy authorizes issuance.</summary>
    public bool IsAllowed { get; init; }
    /// <summary>Hard policy ceiling for the issued deadline.</summary>
    public required DateTimeOffset MaximumExpiresAt { get; init; }
    /// <summary>Stable auditable decision identity.</summary>
    public required string DecisionId { get; init; }
    /// <summary>Canonical approval policy reference when applicable.</summary>
    public string? ApprovalPolicyRef { get; init; }
    /// <summary>Verified approval receipt when policy requires one.</summary>
    public string? ApprovalReceiptId { get; init; }
}

/// <summary>Observation category; reading bytes and reading lifecycle metadata have separate resource requirements.</summary>
public enum JobExecutionLeaseObservationKind
{
    /// <summary>Observe truthful job lifecycle and committed-effect metadata.</summary>
    Status,
    /// <summary>Read derived result or artifact content under current resource authority.</summary>
    OutputContent
}

/// <summary>Lease observation metadata; carries no executor principal or reusable execution capability.</summary>
public sealed record JobExecutionLeaseObservation
{
    /// <summary>Validated job identity.</summary>
    public required string JobId { get; init; }
    /// <summary>Validated lease identity.</summary>
    public required string LeaseId { get; init; }
    /// <summary>Original immutable intent fingerprint.</summary>
    public required string IntentFingerprint { get; init; }
    /// <summary>Canonical executor actor.</summary>
    public required string ExecutorActorId { get; init; }
    /// <summary>Original initiating human actor when present.</summary>
    public string? InitiatingHumanActorId { get; init; }
    /// <summary>Exact tenant binding.</summary>
    public string? TenantId { get; init; }
    /// <summary>Bounded authority deadline.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Exact prepared task authority binding, if any.</summary>
    public JobExecutionLeaseAdmissionBinding? Admission { get; init; }
}
