// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Domain;

/// <summary>
/// Represents the decision result of an access authorization check.
/// </summary>
public readonly record struct AccessDecision(
    bool IsAllowed,
    bool RequiresAuthentication,
    string? FailureReason = null)
{
    /// <summary>
    /// Creates an allowed access decision.
    /// </summary>
    /// <param name="reason">Optional explanatory reason.</param>
    /// <returns>An allowed decision.</returns>
    public static AccessDecision Allowed(string? reason = null) => new(true, false, reason);

    /// <summary>
    /// Creates a forbidden access decision.
    /// </summary>
    /// <param name="reason">Optional explanatory reason.</param>
    /// <returns>A forbidden decision.</returns>
    public static AccessDecision Forbidden(string? reason = null) => new(false, false, reason);

    /// <summary>
    /// Creates a decision that requires authentication.
    /// </summary>
    /// <param name="reason">Optional explanatory reason.</param>
    /// <returns>A decision that requires authentication.</returns>
    public static AccessDecision RequiresAuth(string? reason = null) => new(false, true, reason);
}
