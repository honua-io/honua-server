// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// A request to validate or submit an operation. Carries the target operation id, the
/// caller-supplied parameter values, and the connection/scope context the executor needs.
/// Parameter values are kept as strings so the contract stays AOT-safe and provider-neutral;
/// the executor maps them onto its concrete request type.
/// </summary>
public sealed record OperationRequest
{
    /// <summary>
    /// Operation identifier this request targets (for example <c>service.publish</c>).
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Caller-supplied parameter values keyed by parameter name.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Optional connection identifier the operation targets (for example the secure
    /// connection whose tables are being published).
    /// </summary>
    public string? ConnectionId { get; init; }

    /// <summary>
    /// Optional service name the operation targets.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Optional list of attribute fields, when the operation acts on a subset of columns.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>
    /// When true, the caller requested a dry-run/preview rather than a committed execution
    /// (only honoured when the descriptor's policy advertises <c>SupportsDryRun</c>).
    /// </summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Context passed to <see cref="Abstractions.IOperationExecutor.SubmitAsync"/> and the
/// policy decision point. Carries the resolved connection string and the caller principal
/// identity so executors and policy can act without re-resolving. Tier/role-aware policy
/// uses the principal fields; the no-op default ignores them.
/// </summary>
public sealed record OperationPolicyContext
{
    /// <summary>
    /// Resolved connection string for the target connection, when the operation needs one.
    /// </summary>
    public string? ResolvedConnectionString { get; init; }

    /// <summary>
    /// Stable identifier of the invoking principal, for audit and tier/role policy.
    /// </summary>
    public string? PrincipalId { get; init; }

    /// <summary>
    /// Tier the invoking tenant is on (for example <c>community</c>, <c>pro</c>, <c>enterprise</c>).
    /// Community is pass-through; Pro/Enterprise enforce the policy engine (Phase 4).
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// Role(s) granted to the invoking principal (for example <c>operator</c>, <c>publisher</c>,
    /// <c>viewer</c>). The tier/role-aware policy engine (Phase 4) decides per descriptor
    /// blast-radius/side-effect using both <see cref="Tier"/> and these roles; the no-op
    /// default ignores them. Empty for an unauthenticated or role-less caller.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>
/// Read-only, idempotent result of <see cref="Abstractions.IOperationExecutor.ValidateAsync"/>:
/// a plan/diagnostics view of what the operation would do, without side effects.
/// </summary>
public sealed record OperationValidation
{
    /// <summary>
    /// Whether validation passed without blocking errors.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Machine-readable status (for example <c>valid</c>, <c>warning</c>, <c>invalid</c>).
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Human-readable diagnostic messages describing the plan or the problems found.
    /// </summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>
/// Handle returned by <see cref="Abstractions.IOperationExecutor.SubmitAsync"/>. This is a
/// handle, not a flat result, precisely so a single submit contract can represent a completed
/// synchronous operation, a queued durable job, and a "requires approval" outcome — the three
/// shapes the toolset must model (publish is sync, geoprocessing is job-based, approval lanes
/// return queued/pending).
/// </summary>
public sealed record OperationHandle
{
    /// <summary>
    /// Operation identifier this handle was produced for.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Lifecycle status of the operation.
    /// </summary>
    public required OperationHandleStatus Status { get; init; }

    /// <summary>
    /// Stable handle identifier, used to look the handle's status back up.
    /// </summary>
    public required string HandleId { get; init; }

    /// <summary>
    /// Synchronous result summary, populated only when <see cref="Status"/> is
    /// <see cref="OperationHandleStatus.Completed"/>.
    /// </summary>
    public OperationResultSummary? Result { get; init; }

    /// <summary>
    /// Durable job identifier, populated when the operation was queued as a job.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Approval lane identifier, populated when <see cref="Status"/> is
    /// <see cref="OperationHandleStatus.RequiresApproval"/>.
    /// </summary>
    public string? ApprovalLane { get; init; }

    /// <summary>
    /// Metadata v2 graph revision that resulted from the operation, when it mutated the
    /// canonical metadata graph (for example a publish that created a new layer resource).
    /// </summary>
    public long? MetadataRevision { get; init; }

    /// <summary>
    /// Reason describing a non-completed outcome (deny/require-approval/dry-run/failure).
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Lightweight, serializable summary of a synchronous operation result carried on
/// <see cref="OperationHandle.Result"/>. Operation-specific detail is conveyed as a flat
/// string map so the handle stays AOT-safe and protocol-neutral.
/// </summary>
public sealed record OperationResultSummary
{
    /// <summary>
    /// Human-readable summary of what happened.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Operation-specific detail values keyed by name (for example <c>layerId</c>,
    /// <c>serviceName</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Live status view of a previously submitted operation, returned by
/// <see cref="Abstractions.IOperationExecutor.GetStatusAsync"/>. For the async/job case
/// this is how a caller polls progress after the initial submit returned a queued handle.
/// </summary>
public sealed record OperationStatus
{
    /// <summary>
    /// Operation identifier.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Handle identifier the status is for.
    /// </summary>
    public required string HandleId { get; init; }

    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    public required OperationHandleStatus Status { get; init; }

    /// <summary>
    /// Result summary when the operation has completed.
    /// </summary>
    public OperationResultSummary? Result { get; init; }

    /// <summary>
    /// Durable job identifier when the operation is running as a job.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Metadata v2 revision produced by the operation, when applicable.
    /// </summary>
    public long? MetadataRevision { get; init; }
}

/// <summary>
/// Outcome of <see cref="Abstractions.IOperationPolicyDecisionPoint.EvaluateAsync"/>.
/// </summary>
public enum PolicyDecisionKind
{
    /// <summary>
    /// Allow the operation to execute.
    /// </summary>
    Allow,

    /// <summary>
    /// Require human approval before executing.
    /// </summary>
    RequireApproval,

    /// <summary>
    /// Require a dry-run/preview before a committed execution is permitted.
    /// </summary>
    DryRunFirst,

    /// <summary>
    /// Deny the operation outright.
    /// </summary>
    Deny
}

/// <summary>
/// Decision returned by the policy decision point. The dispatcher consults this before any
/// executor side effect; only <see cref="PolicyDecisionKind.Allow"/> reaches the executor.
/// </summary>
public sealed record PolicyDecision
{
    /// <summary>
    /// The policy outcome.
    /// </summary>
    public required PolicyDecisionKind Kind { get; init; }

    /// <summary>
    /// Optional human-readable reason for the decision.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Approval lane to route to when <see cref="Kind"/> is
    /// <see cref="PolicyDecisionKind.RequireApproval"/>.
    /// </summary>
    public string? ApprovalLane { get; init; }

    /// <summary>
    /// A pass-through allow decision (Community tier default).
    /// </summary>
    public static PolicyDecision Allowed { get; } = new() { Kind = PolicyDecisionKind.Allow };
}
