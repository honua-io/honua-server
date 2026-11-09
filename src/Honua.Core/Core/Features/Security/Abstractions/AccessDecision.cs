// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Features.Security.Abstractions;

public readonly record struct AccessDecision(
    bool IsAllowed,
    bool RequiresAuthentication,
    string? FailureReason = null)
{
    public static AccessDecision Allowed(string? reason = null) => new(true, false, reason);

    public static AccessDecision Forbidden(string? reason = null) => new(false, false, reason);

    public static AccessDecision RequiresAuth(string? reason = null) => new(false, true, reason);

    public static implicit operator DomainAccessDecision(AccessDecision decision)
        => new(decision.IsAllowed, decision.RequiresAuthentication, decision.FailureReason);

    public static implicit operator AccessDecision(DomainAccessDecision decision)
        => new(decision.IsAllowed, decision.RequiresAuthentication, decision.FailureReason);
}
