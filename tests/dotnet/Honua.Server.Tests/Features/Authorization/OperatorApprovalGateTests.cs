using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Authorization;

public sealed class OperatorApprovalGateTests
{
    [UnitTest]
    public void CheckAuthorization_Allowed_ReturnsAllowedDecision()
    {
        var gate = CreateGate(authDecision: AccessDecision.Allowed());
        var principal = CreatePrincipal();

        var result = gate.CheckAuthorization(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        });

        result.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public void CheckAuthorization_Denied_ReturnsForbiddenDecision()
    {
        var gate = CreateGate(authDecision: AccessDecision.Forbidden("no access"));
        var principal = CreatePrincipal();

        var result = gate.CheckAuthorization(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        });

        result.IsAllowed.Should().BeFalse();
        result.RequiresAuthentication.Should().BeFalse();
    }

    [UnitTest]
    public void CheckAuthorization_RequiresAuth_ReturnsRequiresAuthDecision()
    {
        var gate = CreateGate(authDecision: AccessDecision.RequiresAuth());
        var principal = CreatePrincipal();

        var result = gate.CheckAuthorization(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        });

        result.IsAllowed.Should().BeFalse();
        result.RequiresAuthentication.Should().BeTrue();
    }

    [UnitTest]
    public void CheckApproval_NotRequired_ReturnsNotRequired()
    {
        var gate = CreateGate(approval: ApprovalRequirement.NotRequired());
        var principal = CreatePrincipal();

        var result = gate.CheckApproval(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        });

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void CheckApproval_Required_ReturnsRequiredWithPolicyRef()
    {
        var approval = ApprovalRequirement.Required("operator.publish", "publish-requires-approval");
        var gate = CreateGate(approval: approval);
        var principal = CreatePrincipal();

        var result = gate.CheckApproval(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        });

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.publish");
        result.ReasonCodes.Should().Contain("publish-requires-approval");
    }

    [UnitTest]
    public void CheckApproval_DestructiveAction_ReturnsRequiredWithDestructivePolicy()
    {
        var approval = ApprovalRequirement.Required("operator.destructive.job", "destructive-action-requires-approval");
        var gate = CreateGate(approval: approval);
        var principal = CreatePrincipal();

        var result = gate.CheckApproval(principal, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        });

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.destructive.job");
    }

    private static OperatorApprovalGate CreateGate(
        AccessDecision? authDecision = null,
        ApprovalRequirement? approval = null)
    {
        var authEvaluator = new StubAuthEvaluator(authDecision ?? AccessDecision.Allowed());
        var approvalEvaluator = new StubApprovalEvaluator(approval ?? ApprovalRequirement.NotRequired());
        return new OperatorApprovalGate(
            authEvaluator,
            approvalEvaluator,
            NullLogger<OperatorApprovalGate>.Instance);
    }

    private static ClaimsPrincipal CreatePrincipal(string userId = "user-1")
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            "TestScheme");
        return new ClaimsPrincipal(identity);
    }

    private sealed class StubAuthEvaluator(AccessDecision decision) : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
    }

    private sealed class StubApprovalEvaluator(ApprovalRequirement approval) : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
            => approval;
    }
}
