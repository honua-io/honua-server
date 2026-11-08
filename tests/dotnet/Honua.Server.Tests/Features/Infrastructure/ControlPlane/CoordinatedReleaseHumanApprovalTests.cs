// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Tests.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class CoordinatedReleaseHumanApprovalTests
{
    [UnitTheory]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, true)]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, false)]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, true)]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, false)]
    public async Task VerifyGateAsync_LiveHuman_BindsRequesterOrOtherAuthorizedPerson(
        CoordinatedReleaseStep gate, bool requesterApproves)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var actor = CanonicalSecurityActor.Resolve(human.Principal)!.ActorId;
        var operation = Workflow(gate) with
        {
            Audit = new OperationAuditInfo { RequestedBy = requesterApproves ? actor : "another-requester", TenantId = "tenant-a" },
        };
        host.GateReviewRequest(human, operation);
        var review = await host.Authority.OpenGateReviewAsync(operation, gate, host.Context);
        host.GateApproveRequest(human, operation, review);

        var evidence = await host.Authority.VerifyGateAsync(operation, gate,
            OperationHumanApprovalTestHost.Approval(human), default);

        evidence.ActorId.Should().Be(actor);
        evidence.WorkflowId.Should().Be(operation.OperationId);
        evidence.WorkflowVersion.Should().Be(operation.Version);
        evidence.Gate.Should().Be(gate);
        evidence.PlanHash.Should().Be(review.PlanHash);
        evidence.SessionReference.Should().HaveLength(64).And.NotBe(human.SessionId);
        evidence.CapabilityReference.Should().HaveLength(64);
        var approved = operation with
        {
            Version = operation.Version + 1,
            Status = WorkflowOperationStatus.Reconciling,
            CoordinatedRelease = operation.CoordinatedRelease! with
            {
                ContainerHumanApproval = gate == CoordinatedReleaseStep.ContainerRollout ? evidence : null,
                DataHumanApproval = gate == CoordinatedReleaseStep.MetadataAndSchema ? evidence : null,
            },
        };
        CoordinatedReleaseApprovalBinding.HasExecutionAuthority(approved, gate).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("workflow")]
    [InlineData("version")]
    [InlineData("gate")]
    [InlineData("tenant")]
    [InlineData("environment")]
    [InlineData("container")]
    [InlineData("script")]
    [InlineData("requester")]
    [InlineData("package")]
    public async Task VerifyGateAsync_ReviewedTargetChanged_RefusesDecision(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var operation = Workflow();
        host.GateReviewRequest(human, operation);
        var review = await host.Authority.OpenGateReviewAsync(operation, operation.CoordinatedRelease!.CurrentStep, host.Context);
        var changed = ChangedWorkflow(operation, change);
        host.GateApproveRequest(human, changed, review);

        var verify = () => host.Authority.VerifyGateAsync(changed, changed.CoordinatedRelease!.CurrentStep,
            OperationHumanApprovalTestHost.Approval(human), default);

        await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTheory]
    [InlineData("key")]
    [InlineData("bearer")]
    [InlineData("mcp")]
    [InlineData("query")]
    public async Task OpenGateReviewAsync_NonHumanCredentialWithHumanCookie_RefusesReview(string credential)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var operation = Workflow();
        host.GateReviewRequest(human, operation);
        switch (credential)
        {
            case "key": host.Context.Request.Headers["X-API-Key"] = "agent-key"; break;
            case "bearer": host.Context.Request.Headers.Authorization = "Bearer agent-token"; break;
            case "mcp": host.Context.Request.Path = "/mcp"; break;
            case "query": host.Context.Request.QueryString = new("?token=agent-token"); break;
        }

        var review = () => host.Authority.OpenGateReviewAsync(operation, operation.CoordinatedRelease!.CurrentStep, host.Context);

        await review.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTheory]
    [InlineData("expired")]
    [InlineData("logout")]
    [InlineData("session")]
    public async Task VerifyGateAsync_ReviewedSessionLost_RefusesDecision(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var operation = Workflow();
        host.GateReviewRequest(human, operation);
        var gate = operation.CoordinatedRelease!.CurrentStep;
        var review = await host.Authority.OpenGateReviewAsync(operation, gate, host.Context);
        if (change == "expired") host.Clock.Advance(TimeSpan.FromMinutes(6));
        if (change == "logout") await host.Sessions.RemoveAuthenticatedSessionAsync(human.SessionId, default);
        var current = change == "session" ? await host.CreateHumanAsync() : human;
        host.GateApproveRequest(current, operation, review);

        var verify = () => host.Authority.VerifyGateAsync(operation, gate,
            OperationHumanApprovalTestHost.Approval(current), default);

        await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTest]
    public async Task VerifyGateAsync_ProposalCapability_RefusesCrossPurposeDecision()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        host.ReviewRequest(human);
        var proposalReview = await host.Authority.OpenReviewAsync(OperationHumanApprovalTestHost.Proposal(), host.Context);
        var operation = Workflow();
        host.GateApproveRequest(human, operation, new CoordinatedReleaseHumanReview
        {
            WorkflowId = operation.OperationId, WorkflowVersion = operation.Version,
            Gate = operation.CoordinatedRelease!.CurrentStep, PlanHash = proposalReview.PlanHash,
            ExpiresAt = proposalReview.ExpiresAt, Capability = proposalReview.Capability,
        });

        var verify = () => host.Authority.VerifyGateAsync(operation, operation.CoordinatedRelease.CurrentStep,
            OperationHumanApprovalTestHost.Approval(human), default);

        await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    private static WorkflowOperationRecord ChangedWorkflow(WorkflowOperationRecord operation, string change)
    {
        var context = operation.CoordinatedRelease!;
        return change switch
        {
            "workflow" => operation with { OperationId = "different-workflow" },
            "version" => operation with { Version = operation.Version + 1 },
            "requester" => operation with { Audit = operation.Audit with { RequestedBy = "different-requester" } },
            "tenant" => operation with { Audit = operation.Audit with { TenantId = "tenant-b" },
                CoordinatedRelease = context with { RequesterTenantId = "tenant-b" } },
            "gate" => operation with { CoordinatedRelease = context with { CurrentStep = CoordinatedReleaseStep.MetadataAndSchema } },
            "environment" => operation with { CoordinatedRelease = context with { TargetEnvironment = "different-environment" } },
            "container" => operation with { CoordinatedRelease = context with { Container = context.Container with { DesiredImage = "different-image" } } },
            "script" => operation with { CoordinatedRelease = context with { MetadataPlan = context.MetadataPlan with
                { Script = context.MetadataPlan.Script with { ForwardOperations = [] } } } },
            "package" => operation with { CoordinatedRelease = context with { PackageId = "different-package" } },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
    }

    private static WorkflowOperationRecord Workflow(CoordinatedReleaseStep gate = CoordinatedReleaseStep.ContainerRollout) => new()
    {
        OperationId = "coordinated-workflow", Version = 5, Kind = WorkflowOperationKind.CoordinatedRelease,
        Status = WorkflowOperationStatus.AwaitingApproval, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new OperationAuditInfo { RequestedBy = "requester", TenantId = "tenant-a" },
        CoordinatedRelease = new CoordinatedReleaseContext
        {
            PackageId = "reviewed-package", TargetEnvironment = "production", RequesterTenantId = "tenant-a", CurrentStep = gate,
            Container = new CoordinatedReleaseContainerSpec { TargetId = "deploy-target", DesiredImage = "image:v2" },
            MetadataPlan = new MetadataReleaseExecutionPlan
            {
                PackageId = "reviewed-package", TargetEnvironment = "production", ResourceSemanticId = "parcels", NewFieldName = "owner",
                Script = new MetadataReleaseScript { ScriptId = "add-owner", ForwardOperations = [new MetadataReleaseScriptOperation
                {
                    Kind = MetadataReleaseScriptOperationKind.AddColumn, ResourceSemanticId = "parcels", FieldName = "owner", FieldType = "String",
                }] },
            },
        },
    };
}
