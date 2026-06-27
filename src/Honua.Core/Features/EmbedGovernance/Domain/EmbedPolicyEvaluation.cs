// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Reason an embed policy request was denied.
/// </summary>
public enum EmbedPolicyDenyReason
{
    /// <summary>The request was allowed.</summary>
    None = 0,

    /// <summary>The key is revoked or expired.</summary>
    KeyInactive = 1,

    /// <summary>The browser origin is not in the key's allowed-origin list.</summary>
    OriginNotAllowed = 2,

    /// <summary>The requested service is not permitted by the key scope.</summary>
    ServiceNotAllowed = 3,

    /// <summary>The requested content id is not permitted by the key scope.</summary>
    ContentNotAllowed = 4,

    /// <summary>The asserted tenant does not match the key's bound tenant.</summary>
    TenantMismatch = 5,

    /// <summary>The key has exhausted its rate budget for the current window.</summary>
    RateLimited = 6,
}

/// <summary>
/// Inputs the server evaluates an embed key scope against for a single request.
/// </summary>
public sealed record EmbedPolicyRequest
{
    /// <summary>Browser <c>Origin</c> header value of the embed request.</summary>
    public string? Origin { get; init; }

    /// <summary>Service identifier (or origin) the embed is requesting.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Content/item identifier the embed is requesting.</summary>
    public string? ContentId { get; init; }

    /// <summary>Tenant the embed request asserts, when present.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Number of requests already consumed in the current rate window,
    /// including this request. Zero disables the rate-limit check.
    /// </summary>
    public int RequestsConsumedInWindow { get; init; }
}

/// <summary>
/// Result of evaluating an embed policy request against a key scope.
/// </summary>
public sealed record EmbedPolicyDecision
{
    /// <summary>Whether the request is permitted.</summary>
    public required bool Allowed { get; init; }

    /// <summary>The deny reason; <see cref="EmbedPolicyDenyReason.None"/> when allowed.</summary>
    public required EmbedPolicyDenyReason Reason { get; init; }

    /// <summary>Human-readable explanation suitable for audit/log surfaces.</summary>
    public required string Message { get; init; }

    /// <summary>An allowed decision singleton.</summary>
    public static EmbedPolicyDecision Allow { get; } = new()
    {
        Allowed = true,
        Reason = EmbedPolicyDenyReason.None,
        Message = "allowed",
    };

    /// <summary>
    /// Builds a denied decision for the supplied reason and message.
    /// </summary>
    /// <param name="reason">The deny reason.</param>
    /// <param name="message">A human-readable explanation.</param>
    /// <returns>A denied <see cref="EmbedPolicyDecision"/>.</returns>
    public static EmbedPolicyDecision Deny(EmbedPolicyDenyReason reason, string message) => new()
    {
        Allowed = false,
        Reason = reason,
        Message = message,
    };
}
