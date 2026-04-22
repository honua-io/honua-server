// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security;
using Honua.Server.Features.Infrastructure.Authentication;

namespace Honua.Server.Tests.Features.Security;

public sealed class AccessPolicyEvaluatorTests
{
    private readonly AccessPolicyEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_WithNoPolicies_AnonymousPrincipal_RequiresAuth()
    {
        var decision = _evaluator.Evaluate(
            new ClaimsPrincipal(new ClaimsIdentity()),
            layerPolicy: null,
            servicePolicy: null);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WithRestrictedLayerPolicy_AnonymousPrincipal_RequiresAuth()
    {
        var decision = _evaluator.Evaluate(
            new ClaimsPrincipal(new ClaimsIdentity()),
            new AccessPolicy { AllowAnonymous = false },
            servicePolicy: null);

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WithPermissiveLayerAndRestrictedService_DeniesAnonymousRead()
    {
        var decision = _evaluator.Evaluate(
            new ClaimsPrincipal(new ClaimsIdentity()),
            new AccessPolicy { AllowAnonymous = true },
            new AccessPolicy { AllowedRoles = ["service-reader"] });

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WithConflictingRoles_RequiresBothPolicies()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, "layer-reader")],
                authenticationType: "Test"));

        var decision = _evaluator.Evaluate(
            principal,
            new AccessPolicy { AllowedRoles = ["layer-reader"] },
            new AccessPolicy { AllowedRoles = ["service-reader"] });

        decision.IsAllowed.Should().BeFalse();
        decision.RequiresAuthentication.Should().BeFalse();
    }
}
