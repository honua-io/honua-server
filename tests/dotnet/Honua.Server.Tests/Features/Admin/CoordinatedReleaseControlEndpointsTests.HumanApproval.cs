// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class CoordinatedReleaseControlEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    public async Task OpenGateReview_InteractiveHuman_ReturnsBoundReviewWithoutApproving()
    {
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity);
        foreach (var (gate, route) in new[]
        {
            (CoordinatedReleaseStep.ContainerRollout, "container"),
            (CoordinatedReleaseStep.MetadataAndSchema, "data"),
        })
        {
            var operation = await PrepareHumanGateAsync(gate);

            using var response = await human.Client.GetAsync(
                $"/api/v1/admin/metadata/coordinated-releases/operations/{operation.OperationId}/approval-review/{route}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var snapshot = await response.Content.ReadFromJsonAsync(
                CoordinatedReleaseJsonContext.Default.CoordinatedReleaseHumanReviewResponse);
            snapshot.Should().NotBeNull();
            snapshot!.Review.WorkflowId.Should().Be(operation.OperationId);
            snapshot.Review.WorkflowVersion.Should().Be(operation.Version);
            snapshot.Review.PlanHash.Should().Be(CoordinatedReleaseApprovalBinding.ComputePlanHash(operation, gate));
            snapshot.Plan.MetadataPlan.Should().BeEquivalentTo(operation.CoordinatedRelease!.MetadataPlan);
            (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(operation);
        }
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    public async Task OpenGateReview_ApiKey_RefusesWithoutChangingTheGate()
    {
        foreach (var (gate, route) in new[]
        {
            (CoordinatedReleaseStep.ContainerRollout, "container"),
            (CoordinatedReleaseStep.MetadataAndSchema, "data"),
        })
        {
            var operation = await PrepareHumanGateAsync(gate);

            using var response = await _client.GetAsync(
                $"/api/v1/admin/metadata/coordinated-releases/operations/{operation.OperationId}/approval-review/{route}");

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await response.Content.ReadAsStringAsync()).Should().Contain("interactive");
            (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(operation);
        }
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, "container")]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, "data")]
    public async Task ApproveGate_UnchangedReconciliation_PreservesHumanReview(CoordinatedReleaseStep gate, string route)
    {
        var operation = await PrepareHumanGateAsync(gate);
        var reconciler = _fixture.Services.GetRequiredService<ICoordinatedReleaseReconciler>();
        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);
        var reviewed = (await _workflowStore.GetAsync(operation.OperationId))!;
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity);
        var review = await human.OpenGateReviewAsync(operation.OperationId, route);

        for (var poll = 0; poll < 3; poll++)
        {
            await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);
        }

        (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(reviewed);
        using var response = await human.SubmitGateReviewAsync(operation.OperationId, route, review);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);
        _container.StartCalls.Should().Be(gate == CoordinatedReleaseStep.ContainerRollout ? 1 : 0);
        _metadata.StartCalls.Should().Be(gate == CoordinatedReleaseStep.MetadataAndSchema ? 1 : 0);
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, "container")]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, "data")]
    public async Task ApproveGate_PlanChangesDuringReview_RefusesStaleReview(CoordinatedReleaseStep gate, string route)
    {
        var operation = await PrepareHumanGateAsync(gate);
        var reconciler = _fixture.Services.GetRequiredService<ICoordinatedReleaseReconciler>();
        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);
        var reviewed = (await _workflowStore.GetAsync(operation.OperationId))!;
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity);
        var review = await human.OpenGateReviewAsync(operation.OperationId, route);
        var changed = reviewed with
        {
            CoordinatedRelease = reviewed.CoordinatedRelease! with
            {
                Container = reviewed.CoordinatedRelease.Container with { DesiredImage = "honua/server:changed" },
            },
        };
        (await _workflowStore.TrySetAsync(changed)).Should().BeTrue();
        var revised = (await _workflowStore.GetAsync(operation.OperationId))!;

        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);
        using var response = await human.SubmitGateReviewAsync(operation.OperationId, route, review);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(revised);
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, "container")]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, "data")]
    public async Task ApproveGate_ConcurrentHumanReviews_OnlyOneVersionWins(CoordinatedReleaseStep gate, string route)
    {
        var operation = await PrepareHumanGateAsync(gate);
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity);
        var first = await human.OpenGateReviewAsync(operation.OperationId, route);
        var second = await human.OpenGateReviewAsync(operation.OperationId, route);
        first.Plan.MetadataPlan.Should().BeEquivalentTo(operation.CoordinatedRelease!.MetadataPlan);
        first.Review.WorkflowVersion.Should().Be(operation.Version);
        first.Review.PlanHash.Should().Be(CoordinatedReleaseApprovalBinding.ComputePlanHash(operation, gate));

        var responses = await Task.WhenAll(human.SubmitGateReviewAsync(operation.OperationId, route, first),
            human.SubmitGateReviewAsync(operation.OperationId, route, second));
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(response => response.IsSuccessStatusCode).Should().Be(1);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        var stored = (await _workflowStore.GetAsync(operation.OperationId))!;
        stored.Version.Should().Be(operation.Version + 1);
        CoordinatedReleaseApprovalBinding.HasExecutionAuthority(stored, gate).Should().BeTrue();
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approval-review/{gate}")]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, "container")]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, "data")]
    public async Task ApproveGate_ApprovalPersistenceFails_StartsNeitherChild(CoordinatedReleaseStep gate, string route)
    {
        var operation = await PrepareHumanGateAsync(gate);
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity);
        var review = await human.OpenGateReviewAsync(operation.OperationId, route);
        _workflowStore.RefuseWrites = true;

        using var response = await human.SubmitGateReviewAsync(operation.OperationId, route, review);

        response.IsSuccessStatusCode.Should().BeFalse();
        (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(operation);
        await _fixture.Services.GetRequiredService<ICoordinatedReleaseReconciler>()
            .ReconcileCoordinatedReleaseAsync(operation.OperationId);
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations/{operationId}/approve/{gate}")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout, "container")]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema, "data")]
    public async Task ApproveGate_AdminApiKey_RefusesBeforeGateWrite(
        CoordinatedReleaseStep gate, string route)
    {
        var operation = await PrepareHumanGateAsync(gate);

        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/metadata/coordinated-releases/operations/{operation.OperationId}/approve/{route}",
            new { reason = "reviewed by the requester" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("interactive").And.Contain("Console");
        (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(operation);
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout)]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema)]
    public async Task ApproveGateAsync_ActorStringWithoutHumanSession_RefusesBeforeGateWrite(
        CoordinatedReleaseStep gate)
    {
        var operation = await PrepareHumanGateAsync(gate);
        var service = _fixture.Services.GetRequiredService<CoordinatedReleaseControlService>();

        var approve = () => service.ApproveGateAsync(operation.OperationId, gate, "admin", "reviewed");

        await approve.Should().ThrowAsync<HumanApprovalRequiredException>();
        (await _workflowStore.GetAsync(operation.OperationId)).Should().BeEquivalentTo(operation);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout)]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema)]
    public async Task ReconcileAsync_UnclaimedLegacyGateBoolean_RequiresFreshHumanReview(
        CoordinatedReleaseStep gate)
    {
        var operation = await PrepareHumanGateAsync(gate);
        await _workflowStore.SetAsync(operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            CoordinatedRelease = operation.CoordinatedRelease! with
            {
                ContainerGateApproved = true,
                DataGateApproved = true,
                GateApprovedBy = "historical-actor",
            },
        });
        var reconciler = _fixture.Services.GetRequiredService<ICoordinatedReleaseReconciler>();

        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);

        (await _workflowStore.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.AwaitingApproval);
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/metadata/coordinated-releases/operations")]
    [InlineData(CoordinatedReleaseStep.ContainerRollout)]
    [InlineData(CoordinatedReleaseStep.MetadataAndSchema)]
    public async Task ReconcileAsync_ClaimedLegacyChild_DrainsWithoutNewApprovalEvidence(
        CoordinatedReleaseStep gate)
    {
        var operation = await PrepareHumanGateAsync(gate);
        await _workflowStore.SetAsync(operation with
        {
            Status = WorkflowOperationStatus.Reconciling,
            CoordinatedRelease = operation.CoordinatedRelease! with
            {
                ContainerGateApproved = true,
                DataGateApproved = true,
                ContainerOperationId = gate == CoordinatedReleaseStep.ContainerRollout ? "claimed-container" : null,
                MetadataOperationId = gate == CoordinatedReleaseStep.MetadataAndSchema ? "claimed-metadata" : null,
                GateApprovedBy = "historical-actor",
            },
        });
        var reconciler = _fixture.Services.GetRequiredService<ICoordinatedReleaseReconciler>();

        await reconciler.ReconcileCoordinatedReleaseAsync(operation.OperationId);

        var stored = (await _workflowStore.GetAsync(operation.OperationId))!;
        stored.CoordinatedRelease!.CurrentStep.Should().Be(gate == CoordinatedReleaseStep.ContainerRollout
            ? CoordinatedReleaseStep.MetadataAndSchema : CoordinatedReleaseStep.Smoke);
        _container.StartCalls.Should().Be(0);
        _metadata.StartCalls.Should().Be(0);
        stored.CoordinatedRelease.GateApprovedBy.Should().Be("historical-actor");
    }

    private async Task<WorkflowOperationRecord> PrepareHumanGateAsync(CoordinatedReleaseStep gate)
    {
        using var response = await CreateAsync($"human-gate-{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operationId = document.RootElement.GetProperty("operationId").GetString()!;
        var operation = (await _workflowStore.GetAsync(operationId))!;
        operation = operation with
        {
            Status = WorkflowOperationStatus.AwaitingApproval,
            CoordinatedRelease = operation.CoordinatedRelease! with { CurrentStep = gate },
        };
        await _workflowStore.SetAsync(operation);
        return operation;
    }
}
