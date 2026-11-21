// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
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
