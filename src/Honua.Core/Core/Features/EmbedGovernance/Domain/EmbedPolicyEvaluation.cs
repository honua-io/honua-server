// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

public enum EmbedPolicyDenyReason
{
    None = 0,

    KeyInactive = 1,

    OriginNotAllowed = 2,

    ServiceNotAllowed = 3,

    ContentNotAllowed = 4,

    TenantMismatch = 5,

    RateLimited = 6,
}

public sealed record EmbedPolicyRequest
{
    public string? Origin { get; init; }

    public string? ServiceId { get; init; }

    public string? ContentId { get; init; }

    public string? TenantId { get; init; }

    public int RequestsConsumedInWindow { get; init; }
}

public sealed record EmbedPolicyDecision
{
    public required bool Allowed { get; init; }

    public required EmbedPolicyDenyReason Reason { get; init; }

    public required string Message { get; init; }

    public static EmbedPolicyDecision Allow { get; } = new()
    {
        Allowed = true,
        Reason = EmbedPolicyDenyReason.None,
        Message = "allowed",
    };

    public static EmbedPolicyDecision Deny(EmbedPolicyDenyReason reason, string message) => new()
    {
        Allowed = false,
        Reason = reason,
        Message = message,
    };
}
