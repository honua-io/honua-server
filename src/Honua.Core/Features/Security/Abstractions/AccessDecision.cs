// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Compatibility wrapper for callers that still reference
/// <c>Honua.Core.Features.Security.Abstractions.AccessDecision</c>.
/// </summary>
public readonly record struct AccessDecision(
    bool IsAllowed,
    bool RequiresAuthentication,
    string? FailureReason = null)
{
    /// <summary>
    /// Creates an allowed decision.
    /// </summary>
    public static AccessDecision Allowed(string? reason = null) => new(true, false, reason);

    /// <summary>
    /// Creates a forbidden decision.
    /// </summary>
    public static AccessDecision Forbidden(string? reason = null) => new(false, false, reason);

    /// <summary>
    /// Creates a decision that requires authentication.
    /// </summary>
    public static AccessDecision RequiresAuth(string? reason = null) => new(false, true, reason);

    /// <summary>
    /// Converts the compatibility wrapper to the domain access-decision type.
    /// </summary>
    public static implicit operator DomainAccessDecision(AccessDecision decision)
        => new(decision.IsAllowed, decision.RequiresAuthentication, decision.FailureReason);

    /// <summary>
    /// Converts the domain access-decision type to the compatibility wrapper.
    /// </summary>
    public static implicit operator AccessDecision(DomainAccessDecision decision)
        => new(decision.IsAllowed, decision.RequiresAuthentication, decision.FailureReason);
}
