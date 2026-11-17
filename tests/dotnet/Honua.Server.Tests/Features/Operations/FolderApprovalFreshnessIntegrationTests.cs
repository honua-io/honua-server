// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    private const string CatalogChangedReason = "The catalog changed after this folder proposal was submitted. Refresh the catalog and submit a fresh proposal.";

    [IntegrationTheory]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("GET /api/v1/operations/handles/{handleId}")]
    [InlineData("honua_create_folder", "folder")]
    [InlineData("honua_create_folder", "publish")]
    [InlineData("honua_update_folder", "folder")]
    [InlineData("honua_update_folder", "publish")]
    [InlineData("honua_delete_folder", "folder")]
    [InlineData("honua_delete_folder", "publish")]
    [InlineData("honua_move_service", "folder")]
    [InlineData("honua_move_service", "publish")]
    public async Task ApproveFolderProposal_UnrelatedCatalogChange_ReportsConflictToRequester(string tool, string change)
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Target", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var arguments = PendingFolderArguments(tool, before.Graph.Folders.Single().Metadata.Id, before.Etag);
        var accepted = await CallMcpToolAsync(tool, arguments);
        accepted.GetProperty("status").GetString().Should().Be("RequiresApproval");
        var proposalId = accepted.GetProperty("proposalId").GetString()!;
        var operationId = accepted.GetProperty("operationInstanceId").GetString()!;
        var pending = (await _proposals.GetAsync(proposalId))!;

        await ChangeUnrelatedCatalogAsync(change, before.Etag);
        var changed = await SnapshotAsync();
        changed.Etag.Should().NotBe(before.Etag);

        await AssertCatalogConflictAsync(pending);
        await AssertCatalogUnchangedAsync(changed);
        await AssertRequesterCatalogConflictAsync(operationId, proposalId);

        arguments["If-Match"] = changed.Etag;
        var refreshed = await CallMcpToolAsync(tool, arguments);
        var refreshedId = refreshed.GetProperty("proposalId").GetString()!;
        refreshedId.Should().NotBe(proposalId);
        await ApproveAsync(refreshedId);
        (await _proposals.GetAsync(refreshedId))!.Status.Should().Be(OperationProposalStatus.Succeeded);
        await AssertRequesterCatalogConflictAsync(operationId, proposalId);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData("honua_create_folder")]
    [InlineData("honua_update_folder")]
    [InlineData("honua_delete_folder")]
    [InlineData("honua_move_service")]
    public async Task ApproveFolderProposal_CatalogUnchanged_AppliesReviewedMutation(string tool)
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Target", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var arguments = PendingFolderArguments(tool, before.Graph.Folders.Single().Metadata.Id, before.Etag);
        var accepted = await CallMcpToolAsync(tool, arguments);
        accepted.GetProperty("status").GetString().Should().Be("RequiresApproval");
        var proposalId = accepted.GetProperty("proposalId").GetString()!;
        await AssertCatalogUnchangedAsync(before);

        await ApproveAsync(proposalId);

        var completed = (await _proposals.GetAsync(proposalId))!;
        completed.Status.Should().Be(OperationProposalStatus.Succeeded);
        completed.ResolutionReason.Should().BeNull();
        (await SnapshotAsync()).Etag.Should().NotBe(before.Etag);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("GET /api/v1/operations/handles/{handleId}")]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ApproveFolderProposal_CatalogChangesDuringMembershipCheck_ReportsConflict(int readsBeforeChange)
    {
        var proposal = await CreateSubjectFolderProposalAsync();
        var before = await SnapshotAsync();
        _memberships.ReadsBeforeCatalogChange = readsBeforeChange;
        _memberships.ChangeCatalog = AdvanceCatalogAsync;

        await AssertCatalogConflictAsync(proposal);

        _memberships.ChangeCatalog.Should().BeNull("the change must occur inside the approval check");
        var after = await SnapshotAsync();
        after.Etag.Should().NotBe(before.Etag);
        after.Graph.Folders.Should().ContainSingle(folder => folder.Metadata.Name == "Concurrent");
        after.Graph.Folders.Should().NotContain(folder => folder.Metadata.Name == "Pending");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveFolderProposal_CatalogChangesDuringServiceAuthorization_ReportsSaveConflict()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Target", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var proposalId = await ProposeAsync("service.move", new()
        {
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id,
            ["If-Match"] = before.Etag
        });
        var proposal = (await _proposals.GetAsync(proposalId))!;
        _authorization.BeforeServiceAuthorization = AdvanceCatalogAsync;

        await AssertCatalogConflictAsync(proposal);

        _authorization.BeforeServiceAuthorization.Should().BeNull();
        var after = await SnapshotAsync();
        after.Graph.Folders.Should().Contain(folder => folder.Metadata.Name == "Concurrent");
        after.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Should().OnlyContain(service => service.FolderId == null);
        await AssertRequesterCatalogConflictAsync(proposal.Audit.OperationInstanceId!, proposalId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveFolderProposal_AuthorityAndCatalogChange_PreservesAuthorizationRefusal()
    {
        var proposal = await CreateSubjectFolderProposalAsync();
        await AdvanceCatalogAsync();
        var changed = await SnapshotAsync();
        _memberships.Current = new(false, ["admin"]);

        await ApproveAsync(proposal.ProposalId);

        var failed = (await _proposals.GetAsync(proposal.ProposalId))!;
        failed.Status.Should().Be(OperationProposalStatus.Failed);
        failed.ResolutionReason.Should().Be(FolderApprovalAuthorization.FreshProposalRequired);
        await AssertCatalogUnchangedAsync(changed);
    }

    private async Task AssertCatalogUnchangedAsync(MetadataV2GraphSnapshot expected)
    {
        var actual = await SnapshotAsync();
        actual.Etag.Should().Be(expected.Etag);
        JsonSerializer.Serialize(actual.Graph, MetadataV2JsonContext.Default.MetadataV2Graph).Should().Be(
            JsonSerializer.Serialize(expected.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
    }

    private async Task ChangeUnrelatedCatalogAsync(string change, string etag)
    {
        if (change == "publish")
            await ApprovePublishToolAsync(await PublishArgumentsAsync(null));
        else
            await ApplyAsync("folder.create", new() { ["name"] = "Unrelated", ["If-Match"] = etag });
    }

    private async Task AdvanceCatalogAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var before = await store.GetCurrentAsync();
        var mutation = FolderMutation.Create(before.Graph, new FolderRequest { Name = "Concurrent" }, null);
        await store.SaveAsync(mutation.Graph, before.Etag);
    }

    private async Task<OperationProposal> CreateSubjectFolderProposalAsync()
    {
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create",
            TenantId = "public",
            SchemaName = _fixture.CurrentSchema,
            Parameters = new() { ["name"] = "Pending", ["If-Match"] = before.Etag }
        };
        OperationProposal? proposal = null;
        await WithRequesterContextAsync(payload, async context =>
        {
            var result = await CreateRequesterProposalAsync(context, new()
            {
                OperationId = payload.OperationId,
                Kind = OperationClass.AdminConfigChange,
                TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                Plan = new OperationProposalPlan
                {
                    Summary = "Create folder",
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, result.Message);
            proposal = (await _proposals.GetAsync(result.ProposalId!))!;
        }, "folder-requester");
        return proposal!;
    }

    private async Task AssertCatalogConflictAsync(OperationProposal pending)
    {
        using var approval = await _approver.ApproveAsync(pending.ProposalId);
        var response = await approval.Content.ReadAsStringAsync();
        approval.StatusCode.Should().Be(HttpStatusCode.OK, response);
        using var body = JsonDocument.Parse(response);
        body.RootElement.GetProperty("status").GetString().Should().Be("Failed", response);
        body.RootElement.GetProperty("blockingReasons").EnumerateArray().Select(reason => reason.GetString())
            .Should().Contain(CatalogChangedReason);
        var failed = (await _proposals.GetAsync(pending.ProposalId))!;
        failed.Status.Should().Be(OperationProposalStatus.Failed);
        failed.ResolutionReason.Should().Be(CatalogChangedReason);
        failed.ResolvedBy.Should().NotBeNullOrWhiteSpace("the human approval was recorded even though execution was refused");
        failed.Plan.ExecutionPayload.Should().Be(pending.Plan.ExecutionPayload);
        failed.SealedPlanHash.Should().Be(pending.SealedPlanHash, "approval cannot silently rebase the sealed precondition");
    }

    private static Dictionary<string, string?> PendingFolderArguments(string tool, string folderId, string etag)
        => tool switch
        {
            "honua_create_folder" => new() { ["name"] = "Pending", ["If-Match"] = etag },
            "honua_update_folder" => new() { ["id"] = folderId, ["title"] = "Pending title", ["If-Match"] = etag },
            "honua_delete_folder" => new() { ["id"] = folderId, ["If-Match"] = etag },
            "honua_move_service" => new() { ["serviceName"] = "test", ["folderId"] = folderId, ["If-Match"] = etag },
            _ => throw new ArgumentException("Unknown folder tool", nameof(tool))
        };

    private async Task AssertRequesterCatalogConflictAsync(string operationId, string proposalId)
    {
        using var response = await _proposer.GetAsync($"/api/v1/operations/handles/{operationId}");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var body = JsonDocument.Parse(payload);
        var data = body.RootElement.GetProperty("data");
        data.GetProperty("operationInstanceId").GetString().Should().Be(operationId);
        data.GetProperty("proposalId").GetString().Should().Be(proposalId);
        data.GetProperty("status").GetString().Should().Be("Failed");
        data.GetProperty("reason").GetString().Should().Be(CatalogChangedReason);
    }
}
