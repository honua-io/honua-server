// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderMutationServiceTests
{
    [UnitTest]
    public async Task ExecuteAsync_DuplicateDeliveryWithOriginalEtag_DoesNotSaveTwice()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator")], "test"));
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(principal, Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var approvals = Substitute.For<IOperatorApprovalEvaluator>();
        approvals.Evaluate(principal, Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
        var current = new MetadataV2GraphSnapshot(new MetadataV2Graph(), "before", DateTimeOffset.UtcNow);
        var store = Substitute.For<IMetadataV2GraphStore>();
        store.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(_ => current);
        store.SaveAsync(Arg.Any<MetadataV2Graph>(), "before", Arg.Any<CancellationToken>()).Returns(call =>
        {
            current = new MetadataV2GraphSnapshot(call.Arg<MetadataV2Graph>(), "after", DateTimeOffset.UtcNow);
            return Task.FromResult(current);
        });
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddSingleton(new OperatorApprovalGate(authorization, approvals, NullLogger<OperatorApprovalGate>.Instance)).BuildServiceProvider();
        var execution = new FolderMutationExecution
        {
            Services = services, Principal = principal,
            Command = new FolderMutationCommand.Create("survey", default), ExpectedEtag = "before"
        };

        var first = await FolderMutationService.ExecuteAsync(execution);
        var duplicate = await FolderMutationService.ExecuteAsync(execution);

        first.Failure.Should().BeNull();
        first.Saved!.Etag.Should().Be("after");
        duplicate.Failure.Should().BeOfType<FolderExecutionFailure.Mutation>()
            .Which.Refusal.Should().Be(FolderMutationFailure.SnapshotChanged);
        current.Graph.Folders.Should().ContainSingle(folder => folder.Metadata.Name == "survey");
        await store.Received(1).SaveAsync(Arg.Any<MetadataV2Graph>(), "before", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_ApprovedRoleWithoutDurableReplay_RefusesBeforeCatalogRead()
    {
        var store = Substitute.For<IMetadataV2GraphStore>();
        await using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, AdminApiKeyPermission.ApprovedOperationRole)], "test"));

        var result = await FolderMutationService.ExecuteAsync(new FolderMutationExecution
        {
            Services = services, Principal = principal,
            Command = new FolderMutationCommand.Create("survey", default), ExpectedEtag = "revision"
        });

        result.Failure.Should().BeOfType<FolderExecutionFailure.Replay>();
        await store.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ExecuteAsync_ConflictingReplayAdmissions_RefusesBeforeCatalogRead()
    {
        var store = Substitute.For<IMetadataV2GraphStore>();
        await using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var principal = new ClaimsPrincipal();
        var result = await FolderMutationService.ExecuteAsync(new FolderMutationExecution
        {
            Services = services, Principal = principal,
            Command = new FolderMutationCommand.Create("survey", default), ExpectedEtag = "revision",
            Replay = new FolderReplayAdmission
            {
                Principal = principal, OperationId = "folder.create", ProposalId = "proposal", OperationInstanceId = "instance"
            },
            OperationReplay = new FolderOperationReplayAdmission(
                new OperationRequest { OperationId = "folder.create" }, new OperationPolicyContext())
        });
        result.Failure.Should().BeOfType<FolderExecutionFailure.Replay>();
        await store.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WithoutHttpContext_EnforcesApprovalAndSnapshotBeforeSaving(bool approvalRequired)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator")], "test"));
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(principal, Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var approvals = Substitute.For<IOperatorApprovalEvaluator>();
        approvals.Evaluate(principal, Arg.Any<OperatorAuthorizationRequest>())
            .Returns(approvalRequired ? ApprovalRequirement.Required("human", "approval-required") : ApprovalRequirement.NotRequired());
        var store = Substitute.For<IMetadataV2GraphStore>();
        store.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(new MetadataV2Graph(), "new-revision", DateTimeOffset.UtcNow));
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddSingleton(new OperatorApprovalGate(authorization, approvals, NullLogger<OperatorApprovalGate>.Instance)).BuildServiceProvider();

        var result = await FolderMutationService.ExecuteAsync(new FolderMutationExecution
        {
            Services = services, Principal = principal,
            Command = new FolderMutationCommand.Create("survey", default), ExpectedEtag = "old-revision"
        });

        if (approvalRequired)
        {
            result.Failure.Should().BeOfType<FolderExecutionFailure.Approval>();
            await store.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
        }
        else
        {
            result.Failure.Should().BeOfType<FolderExecutionFailure.Mutation>()
                .Which.Refusal.Should().Be(FolderMutationFailure.SnapshotChanged);
        }
        await store.DidNotReceive().SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
