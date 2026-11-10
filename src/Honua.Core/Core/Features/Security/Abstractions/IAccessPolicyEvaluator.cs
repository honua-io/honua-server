// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Security.Domain;
using DomainAccessDecision = Honua.Core.Features.Security.Domain.AccessDecision;

namespace Honua.Core.Features.Security.Abstractions;

public interface IAccessPolicyEvaluator
{
    Task<DomainAccessDecision> EvaluateAsync(ClaimsPrincipal principal, string resource, string action);

    DomainAccessDecision Evaluate(ClaimsPrincipal principal, string resource, string action);

    Task<DomainAccessDecision> EvaluateAsync(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        object? scope = null);

    DomainAccessDecision Evaluate(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        object? scope = null);
}
