// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// Carries everything an operation needs for a single invocation: the input payload,
/// the caller identity, and the tier and role used by the policy decision point.
/// </summary>
/// <remarks>
/// The input is carried as a raw JSON element so the one surface can deserialize a
/// payload (deterministic UI form or AI function-call arguments) without the catalog
/// taking a dependency on each operation's typed request.
/// </remarks>
public sealed record OperationInvocationContext
{
    /// <summary>
    /// Identifier of the operation being invoked.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Raw input payload for the operation. <see langword="null"/> when no body was
    /// supplied.
    /// </summary>
    public JsonElement? Input { get; init; }

    /// <summary>
    /// Caller identity (principal name or subject). May be <see langword="null"/> for
    /// anonymous/service callers.
    /// </summary>
    public string? CallerIdentity { get; init; }

    /// <summary>
    /// Subscription tier the policy decision point evaluates against. Defaults to
    /// <see cref="OperationTier.Community"/> (pass-through).
    /// </summary>
    public OperationTier Tier { get; init; } = OperationTier.Community;

    /// <summary>
    /// Caller role used by a tier- and role-aware policy decision point.
    /// </summary>
    public string? Role { get; init; }
}

/// <summary>
/// Result of invoking an operation: success outputs, the policy lane the invocation
/// landed in, or a structured error.
/// </summary>
public sealed record OperationResult
{
    /// <summary>
    /// Whether the operation executed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Identifier of the operation this result is for.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Policy lane the invocation resolved to. <see cref="OperationPolicyOutcome.Allow"/>
    /// for an executed operation; otherwise the lane the policy decision point routed
    /// the invocation into.
    /// </summary>
    public required OperationPolicyOutcome Outcome { get; init; }

    /// <summary>
    /// Structured success outputs as a raw JSON element when the operation executed.
    /// </summary>
    public JsonElement? Outputs { get; init; }

    /// <summary>
    /// Optional human-readable reason describing a non-allow policy lane or an error.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Structured error code when the operation failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Creates a successful result with the supplied outputs.
    /// </summary>
    /// <param name="operationId">Identifier of the operation.</param>
    /// <param name="outputs">Serialized success outputs.</param>
    public static OperationResult Succeeded(string operationId, JsonElement outputs) => new()
    {
        Success = true,
        OperationId = operationId,
        Outcome = OperationPolicyOutcome.Allow,
        Outputs = outputs
    };

    /// <summary>
    /// Creates a result indicating the policy decision point routed the invocation
    /// into a non-execution lane (require-approval, dry-run-first, or deny).
    /// </summary>
    /// <param name="operationId">Identifier of the operation.</param>
    /// <param name="outcome">The non-allow policy lane.</param>
    /// <param name="reason">Reason supplied by the decision point.</param>
    public static OperationResult FromPolicyLane(
        string operationId,
        OperationPolicyOutcome outcome,
        string? reason) => new()
        {
            Success = false,
            OperationId = operationId,
            Outcome = outcome,
            Reason = reason
        };

    /// <summary>
    /// Creates a failed result for an operation that was allowed but errored during
    /// execution.
    /// </summary>
    /// <param name="operationId">Identifier of the operation.</param>
    /// <param name="errorCode">Stable machine-readable error code.</param>
    /// <param name="reason">Human-readable reason.</param>
    public static OperationResult Failed(string operationId, string errorCode, string reason) => new()
    {
        Success = false,
        OperationId = operationId,
        Outcome = OperationPolicyOutcome.Allow,
        ErrorCode = errorCode,
        Reason = reason
    };
}

/// <summary>
/// Decision returned by a policy decision point for a single invocation. This is the
/// guardrail seam: every invocation flows through it, even when the default decision
/// point is a pass-through.
/// </summary>
public sealed record PolicyDecision
{
    /// <summary>
    /// Lane the invocation is routed into.
    /// </summary>
    public required OperationPolicyOutcome Outcome { get; init; }

    /// <summary>
    /// Optional human-readable reason for the decision.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// A pass-through allow decision (Community-tier behaviour).
    /// </summary>
    public static PolicyDecision Allow { get; } = new() { Outcome = OperationPolicyOutcome.Allow };

    /// <summary>
    /// Creates a decision that routes the invocation to a human approval lane.
    /// </summary>
    /// <param name="reason">Reason for requiring approval.</param>
    public static PolicyDecision RequireApproval(string reason) =>
        new() { Outcome = OperationPolicyOutcome.RequireApproval, Reason = reason };

    /// <summary>
    /// Creates a decision that requires a dry run before a real execution.
    /// </summary>
    /// <param name="reason">Reason for requiring a dry run first.</param>
    public static PolicyDecision DryRunFirst(string reason) =>
        new() { Outcome = OperationPolicyOutcome.DryRunFirst, Reason = reason };

    /// <summary>
    /// Creates a decision that denies the invocation.
    /// </summary>
    /// <param name="reason">Reason for denial.</param>
    public static PolicyDecision Deny(string reason) =>
        new() { Outcome = OperationPolicyOutcome.Deny, Reason = reason };
}
