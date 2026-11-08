// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class CoordinatedReleaseControlEndpointsTests
{
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
