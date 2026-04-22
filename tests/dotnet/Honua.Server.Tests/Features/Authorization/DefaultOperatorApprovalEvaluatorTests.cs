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
        bool destructiveActionsRequireApproval = true,
        bool adminExemptFromApproval = true)
    {
        var options = Options.Create(new OperatorApprovalOptions
        {
            PublishRequiresApproval = publishRequiresApproval,
            DestructiveActionsRequireApproval = destructiveActionsRequireApproval,
            AdminExemptFromApproval = adminExemptFromApproval
        });
        var rbacOptions = Options.Create(new RbacOptions());
        return new DefaultOperatorApprovalEvaluator(
            options,
            rbacOptions,
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
    public void Evaluate_DestructiveExecute_RequiresApprovalWhenEnabled()
    {
        var evaluator = CreateEvaluator(destructiveActionsRequireApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.destructive.process");
        result.ReasonCodes.Should().Contain("destructive-action-requires-approval");
    }

    [UnitTest]
    public void Evaluate_DestructiveExecute_NoApprovalWhenDisabled()
    {
        var evaluator = CreateEvaluator(destructiveActionsRequireApproval: false);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_NonDestructiveExecute_NoApprovalEvenWhenEnabled()
    {
        var evaluator = CreateEvaluator(destructiveActionsRequireApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Process,
            Operation = OperatorOperation.Execute,
            IsDestructive = false
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_AdminPrincipal_ExemptByDefault()
    {
        var evaluator = CreateEvaluator(publishRequiresApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        };

        var result = evaluator.Evaluate(CreateAdminPrincipal(), request);

        result.IsRequired.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_AdminPrincipal_GatedWhenExemptionDisabled()
    {
        var evaluator = CreateEvaluator(publishRequiresApproval: true, adminExemptFromApproval: false);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Publish
        };

        var result = evaluator.Evaluate(CreateAdminPrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.publish");
        result.ReasonCodes.Should().Contain("publish-requires-approval");
    }

    [UnitTest]
    public void Evaluate_DeploymentPublish_IncludesDeployPublishReasonCode()
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
        result.ReasonCodes.Should().Contain("deploy-publish");
    }

    [UnitTest]
    public void Evaluate_NonDeploymentPublish_DoesNotIncludeDeployPublishReasonCode()
    {
        var evaluator = CreateEvaluator(publishRequiresApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Package,
            Operation = OperatorOperation.Publish
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.ReasonCodes.Should().Contain("publish-requires-approval");
        result.ReasonCodes.Should().NotContain("deploy-publish");
    }

    [UnitTest]
    public void Evaluate_DestructiveDeployment_IncludesResourceQualifiedPolicyRef()
    {
        var evaluator = CreateEvaluator(destructiveActionsRequireApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.destructive.deployment");
    }

    [UnitTest]
    public void Evaluate_DestructiveJob_IncludesResourceQualifiedPolicyRef()
    {
        var evaluator = CreateEvaluator(destructiveActionsRequireApproval: true);
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        };

        var result = evaluator.Evaluate(CreatePrincipal(), request);

        result.IsRequired.Should().BeTrue();
        result.PolicyRef.Should().Be("operator.destructive.job");
    }

    private static ClaimsPrincipal CreateAdminPrincipal(string userId = "admin-1")
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("roles", "admin")
            ],
            "TestScheme");
        return new ClaimsPrincipal(identity);
    }
}
