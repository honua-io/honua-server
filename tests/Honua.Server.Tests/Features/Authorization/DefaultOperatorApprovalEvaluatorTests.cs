using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Authorization;

public sealed class DefaultOperatorApprovalEvaluatorTests
{
    private static DefaultOperatorApprovalEvaluator CreateEvaluator(
        bool publishRequiresApproval = true,
        bool destructiveActionsRequireApproval = true)
    {
        var options = Options.Create(new OperatorApprovalOptions
        {
            PublishRequiresApproval = publishRequiresApproval,
            DestructiveActionsRequireApproval = destructiveActionsRequireApproval
        });
        return new DefaultOperatorApprovalEvaluator(
            options,
            NullLogger<DefaultOperatorApprovalEvaluator>.Instance);
    }

    private static ClaimsPrincipal CreatePrincipal(string userId = "user-1")
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            "TestScheme");
        return new ClaimsPrincipal(identity);
    }

    [UnitTest]
    public void Evaluate_PublishOperation_RequiresApprovalWhenEnabled()
    {
        var evaluator = CreateEvaluator(publishRequiresApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.publish");
        result.ReasonCodes.Should().Contain("publish-requires-approval");
    }

    [UnitTest]
    public void Evaluate_PublishOperation_NoApprovalWhenDisabled()
    {
        var evaluator = CreateEvaluator(publishRequiresApproval: false);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_PromoteToPublic_RequiresApproval()
    {
        var evaluator = CreateEvaluator();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Promote,
            WorkspaceVisibility = WorkspaceVisibility.Public
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.promote-wide");
        result.ReasonCodes.Should().Contain("promote-to-wide-visibility");
    }

    [UnitTest]
    public void Evaluate_PromoteToOrganization_RequiresApproval()
    {
        var evaluator = CreateEvaluator();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Promote,
            WorkspaceVisibility = WorkspaceVisibility.Organization
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_PromoteToPersonal_NoApproval()
    {
        var evaluator = CreateEvaluator();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Promote,
            WorkspaceVisibility = WorkspaceVisibility.Personal
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_PromoteToShared_NoApproval()
    {
        var evaluator = CreateEvaluator();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Workspace,
            Operation = OperatorOperation.Promote,
            WorkspaceVisibility = WorkspaceVisibility.Shared
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_ReadOperation_NoApproval()
    {
        var evaluator = CreateEvaluator();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Catalog,
            Operation = OperatorOperation.Read
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_ExecuteOperation_NoApproval()
    {
        var evaluator = CreateEvaluator();
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }
}
