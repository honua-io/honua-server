// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

public sealed class WorkflowRollbackApprovalTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(WorkflowRollbackOperations.Deploy)]
    [InlineData(WorkflowRollbackOperations.CoordinatedRelease)]
    public void ProductionComposition_RollbackHasOneSafeMapper_AndSealsReplay(string operationId)
    {
        var services = new ServiceCollection();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(Substitute.For<IOperationProposalStore>());
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        using var provider = services.BuildServiceProvider();
        var mapper = provider.GetServices<IOperationApprovalRequestMapper>()
            .Should().ContainSingle(candidate => candidate.OperationId == operationId).Subject;
        var descriptor = WorkflowRollbackOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == operationId);
        var parameters = new Dictionary<string, string?>
        {
            [WorkflowRollbackOperations.TargetOperationId] = "accepted-workflow",
            [WorkflowRollbackOperations.Reason] = "Restore the approved revision",
            [WorkflowRollbackOperations.ApprovedDataAffecting] = "True",
            [WorkflowRollbackOperations.ApprovedRequiresApproval] = "True",
        };
        var accepted = new OperationRequest { OperationId = operationId, Parameters = parameters };
        var mapped = mapper.Map(descriptor, accepted, new OperationPolicyContext
        {
            PrincipalId = "operator",
            OperationInstanceId = "instance",
            CorrelationId = "correlation",
            IdempotencyKey = "scoped-key",
            TenantId = "tenant-a",
            SchemaName = "tenant_a",
        }, new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });

        mapped.Kind.Should().Be(OperationClass.Deploy);
        mapped.Plan!.RiskLevel.Should().Be(ProposalRiskLevel.High);
        mapped.IdempotencyKey.Should().Be("scoped-key");
        parameters[WorkflowRollbackOperations.TargetOperationId] = "different-workflow";
        parameters[WorkflowRollbackOperations.ApprovedDataAffecting] = "False";
        var replay = mapper.MapReplay(mapped);
        replay.Request.OperationId.Should().Be(operationId);
        replay.Request.Parameters[WorkflowRollbackOperations.TargetOperationId].Should().Be("accepted-workflow");
        replay.Request.Parameters[WorkflowRollbackOperations.ApprovedDataAffecting].Should().Be("True");
        replay.Request.Parameters[WorkflowRollbackOperations.ApprovedRequiresApproval].Should().Be("True");
        replay.TenantId.Should().Be("tenant-a");
        replay.SchemaName.Should().Be("tenant_a");

        var otherOperationId = operationId == WorkflowRollbackOperations.Deploy
            ? WorkflowRollbackOperations.CoordinatedRelease : WorkflowRollbackOperations.Deploy;
        var otherMapper = provider.GetServices<IOperationApprovalRequestMapper>()
            .Single(candidate => candidate.OperationId == otherOperationId);
        var wrongReplay = () => otherMapper.MapReplay(mapped);
        wrongReplay.Should().Throw<InvalidOperationException>();
    }

    [UnitTest]
    public void DeployApprovalMapper_RejectsMissingSafetyClassification()
    {
        var mapper = new WorkflowRollbackApprovalRequestMapper(WorkflowRollbackOperations.Deploy);
        var descriptor = WorkflowRollbackOperations.BuildDescriptors()
            .Single(candidate => candidate.OperationId == WorkflowRollbackOperations.Deploy);
        var request = new OperationRequest
        {
            OperationId = WorkflowRollbackOperations.Deploy,
            Parameters = new Dictionary<string, string?>
            {
                [WorkflowRollbackOperations.TargetOperationId] = "workflow",
            },
        };
        var map = () => mapper.Map(descriptor, request, new OperationPolicyContext(),
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval });
        map.Should().Throw<ArgumentException>();
    }
}
