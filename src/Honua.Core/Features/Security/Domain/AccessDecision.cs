// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Domain;

/// <summary>
/// Represents the decision result of an access authorization check.
/// </summary>
public enum AccessDecision
{
    /// <summary>
    /// Access is explicitly denied.
    /// </summary>
    Denied = 0,

    /// <summary>
    /// Access is granted.
    /// </summary>
    Granted = 1,

    /// <summary>
    /// Unable to determine access, requires additional context.
    /// </summary>
    Indeterminate = 2
}

/// <summary>
/// Detailed result of an access authorization check.
/// </summary>
public readonly record struct AccessDecisionResult(
    AccessDecision Decision,
    string? Reason = null,
    Dictionary<string, object>? Context = null)
{
    public static AccessDecisionResult Granted(string? reason = null)
        => new(AccessDecision.Granted, reason);

    public static AccessDecisionResult Denied(string? reason = null)
        => new(AccessDecision.Denied, reason);

    public static AccessDecisionResult Indeterminate(string? reason = null)
        => new(AccessDecision.Indeterminate, reason);

    public bool IsGranted => Decision == AccessDecision.Granted;
    public bool IsDenied => Decision == AccessDecision.Denied;
}