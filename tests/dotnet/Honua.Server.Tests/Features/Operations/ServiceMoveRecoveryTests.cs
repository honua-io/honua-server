// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class ServiceMoveRecoveryTests
{
    [UnitTest]
    public void Move_RecordsThePriorFolder_AndRestorePutsItBack()
    {
        var graph = Graph();
        var moved = new FolderMutationCommand.Move("survey", new MetadataV2FolderSelection("archive")).Apply(graph, null);

        moved.PriorAssignments.Should().ContainSingle();
        moved.PriorAssignments[0].PriorFolderId.Should().Be("maps");
        moved.Graph.Services.Single().FolderId.Should().Be("archive");

        var restored = new FolderMutationCommand.Restore("survey", moved.PriorAssignments).Apply(moved.Graph, null);
        restored.Graph.Services.Single().FolderId.Should().Be("maps");
    }

    [UnitTest]
    public void Decide_RefusesAfterTheCatalogChanges_AndAcceptsAnUnchangedOrAlreadyRestoredGraph()
    {
        var graph = Graph();
        var moved = new FolderMutationCommand.Move("survey", new MetadataV2FolderSelection("archive")).Apply(graph, null);
        var evidence = new ServiceMoveEvidenceRecord("survey", "after", moved.PriorAssignments);
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        ServiceMoveEvidence.Write(details, evidence);

        var read = ServiceMoveEvidence.Read(details);
        read.Should().NotBeNull();
        ServiceMoveEvidence.Decide(moved.Graph, "after", read!).Should().Be(ServiceMoveRecoveryKind.Ready);
        ServiceMoveEvidence.Decide(moved.Graph, "published", read).Should().Be(ServiceMoveRecoveryKind.CatalogChanged);
        ServiceMoveEvidence.Decide(graph, "published", read).Should().Be(ServiceMoveRecoveryKind.AlreadyRestored);
    }

    [UnitTest]
    public async Task ExecuteAsync_Restore_RefusesWhenTheRecordedEtagIsStale()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator")], "test"));
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(principal, Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var approvals = Substitute.For<IOperatorApprovalEvaluator>();
        approvals.Evaluate(principal, Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
        var store = Substitute.For<IMetadataV2GraphStore>();
        store.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new MetadataV2GraphSnapshot(Graph(), "current", DateTimeOffset.UtcNow));
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddSingleton(new OperatorApprovalGate(authorization, approvals, NullLogger<OperatorApprovalGate>.Instance))
            .BuildServiceProvider();

        var result = await FolderMutationService.ExecuteAsync(new FolderMutationExecution
        {
            Services = services,
            Principal = principal,
            Command = new FolderMutationCommand.Restore("survey", [new ServiceFolderAssignment("service", "maps")]),
            ExpectedEtag = "recorded"
        });

        result.Failure.Should().BeOfType<FolderExecutionFailure.Mutation>()
            .Which.Refusal.Should().Be(FolderMutationFailure.SnapshotChanged);
        await store.DidNotReceive().SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void ServiceMove_DeclaresSupportedCompensation()
    {
        var compensation = AdminApiOperationCatalog.Descriptors.Single(descriptor => descriptor.OperationId == "service.move").Compensation;
        compensation.Availability.Should().Be(OperationCompensationAvailability.Supported);
        compensation.Reason.Should().Be(ServiceMoveEvidence.SupportedReason);
        AdminApiOperationCatalog.Descriptors.Single(descriptor => descriptor.OperationId == "folder.create").Compensation.Availability
            .Should().Be(OperationCompensationAvailability.Unavailable);
    }

    [UnitTest]
    public async Task PostAsync_CrossTenantHandle_ReturnsNotFoundWithNoStore()
    {
        var instanceStore = Substitute.For<IOperationInstanceStore>();
        instanceStore.GetAsync("opinst-other", Arg.Any<CancellationToken>()).Returns(new OperationHandle
        {
            OperationInstanceId = "opinst-other",
            OperationId = "service.move",
            Status = OperationHandleStatus.Completed,
            CorrelationId = "corr-1",
            TenantId = "tenant-a",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Result = new OperationResultSummary { Summary = "Move service completed." }
        });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AuthenticationExtensions.AdminPolicy)
            .Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-b");
        await using var services = new ServiceCollection()
            .AddSingleton(authorization)
            .AddSingleton(instanceStore)
            .AddSingleton(Substitute.For<IMetadataV2GraphStore>())
            .AddSingleton(tenant)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Admin")], "test"))
        };

        var result = await ServiceMoveCompensation.PostAsync(context, "opinst-other");

        result.Should().BeOfType<NotFound>();
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [UnitTest]
    public async Task PostAsync_AgentCaller_IsRefusedBeforeReadingHandle()
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AuthenticationExtensions.AdminPolicy)
            .Returns(AuthorizationResult.Success());
        var instanceStore = Substitute.For<IOperationInstanceStore>();
        await using var services = new ServiceCollection()
            .AddSingleton(authorization)
            .AddSingleton(instanceStore)
            .AddSingleton(Substitute.For<IMetadataV2GraphStore>())
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "agent-1"), new Claim(ClaimTypes.Role, "Agent")], "test"))
        };

        var result = await ServiceMoveCompensation.PostAsync(context, "opinst-any");

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await instanceStore.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PostAsync_AlreadyRestored_EnforcesOperatorAuthorizationBeforeRecording()
    {
        var graph = Graph();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        ServiceMoveEvidence.Write(details, new ServiceMoveEvidenceRecord("survey", "after", [new ServiceFolderAssignment("service", "maps")]));
        var instanceStore = Substitute.For<IOperationInstanceStore>();
        instanceStore.GetAsync("opinst-restored", Arg.Any<CancellationToken>()).Returns(new OperationHandle
        {
            OperationInstanceId = "opinst-restored",
            OperationId = "service.move",
            Status = OperationHandleStatus.Completed,
            CorrelationId = "corr-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Result = new OperationResultSummary { Summary = "Move service completed.", Details = details }
        });
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AuthenticationExtensions.AdminPolicy)
            .Returns(AuthorizationResult.Success());
        var operatorAuth = Substitute.For<IOperatorAuthorizationEvaluator>();
        operatorAuth.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Forbidden("denied")));
        var approvals = Substitute.For<IOperatorApprovalEvaluator>();
        var catalog = Substitute.For<IMetadataV2GraphStore>();
        catalog.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new MetadataV2GraphSnapshot(graph, "published", DateTimeOffset.UtcNow));
        await using var services = new ServiceCollection()
            .AddSingleton(authorization)
            .AddSingleton(instanceStore)
            .AddSingleton(catalog)
            .AddSingleton(new OperatorApprovalGate(operatorAuth, approvals, NullLogger<OperatorApprovalGate>.Instance))
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, "Admin")], "test"))
        };

        var result = await ServiceMoveCompensation.PostAsync(context, "opinst-restored");

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await instanceStore.DidNotReceive().TryCreateAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void Read_PreservesNamespace_AndRejectsNonObjectAssignmentEntries()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        ServiceMoveEvidence.Write(details, new ServiceMoveEvidenceRecord("survey", "after", [new ServiceFolderAssignment("service", "maps")])
        {
            Namespace = "field-ops"
        });
        var read = ServiceMoveEvidence.Read(details);
        read.Should().NotBeNull();
        read!.Namespace.Should().Be("field-ops");

        details[ServiceMoveEvidence.AssignmentsKey] = "[123]";
        ServiceMoveEvidence.Read(details).Should().BeNull();
    }

    [UnitTest]
    public async Task PersistHttpMoveAsync_WhenInstanceStoreUnavailable_DoesNotFailCommittedMove()
    {
        var store = Substitute.For<IOperationInstanceStore>();
        store.TryCreateAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new CapabilityUnavailableException("operation-instances", "system-database", "configure system-database", "unavailable"));
        await using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var graph = Graph();
        var moved = new FolderMutationCommand.Move("survey", new MetadataV2FolderSelection("archive")).Apply(graph, null);
        var execution = new FolderExecutionResult(new MetadataV2GraphSnapshot(moved.Graph, "after", DateTimeOffset.UtcNow), moved, null);

        var act = () => ServiceMoveEvidence.PersistHttpMoveAsync(context, new FolderMutationCommand.Move("survey", new MetadataV2FolderSelection("archive")), execution);

        await act.Should().NotThrowAsync();
        context.Response.Headers.ContainsKey("X-Honua-Operation-Instance-Id").Should().BeFalse();
    }

    private static MetadataV2Graph Graph() => new()
    {
        Folders =
        [
            new MetadataV2Folder { Metadata = new MetadataV2ObjectMetadata { Id = "maps", Name = "maps" } },
            new MetadataV2Folder { Metadata = new MetadataV2ObjectMetadata { Id = "archive", Name = "archive" } }
        ],
        Services = [new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "survey" }, FolderId = "maps" }]
    };
}
