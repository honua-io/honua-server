// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Evaluates catalog access policies against the current principal.
/// </summary>
public interface IAccessPolicyEvaluator
{
    /// <summary>
    /// Evaluates layer/service access policies and returns an access decision.
    /// </summary>
    /// <param name="principal">The current user principal.</param>
    /// <param name="layerPolicy">Layer-specific policy (highest precedence).</param>
    /// <param name="servicePolicy">Service-level policy (fallback).</param>
    /// <returns>Access decision for the request.</returns>
    AccessDecision Evaluate(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy);
}

/// <summary>
/// Result of an access policy evaluation.
/// </summary>
public sealed record AccessDecision
{
    /// <summary>
    /// Whether the access is allowed.
    /// </summary>
    public bool IsAllowed { get; init; }

    /// <summary>
    /// Whether authentication is required to proceed.
    /// </summary>
    public bool RequiresAuthentication { get; init; }

    /// <summary>
    /// Optional failure reason for diagnostics.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Creates an allowed decision.
    /// </summary>
    public static AccessDecision Allowed() => new() { IsAllowed = true };

    /// <summary>
    /// Creates a decision indicating authentication is required.
    /// </summary>
    public static AccessDecision RequiresAuth(string? reason = null) => new()
    {
        IsAllowed = false,
        RequiresAuthentication = true,
        FailureReason = reason
    };

    /// <summary>
    /// Creates a decision indicating access is forbidden.
    /// </summary>
    public static AccessDecision Forbidden(string? reason = null) => new()
    {
        IsAllowed = false,
        RequiresAuthentication = false,
        FailureReason = reason
    };
}
