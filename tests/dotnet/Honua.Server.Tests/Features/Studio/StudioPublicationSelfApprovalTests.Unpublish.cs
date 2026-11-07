// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPublicationSelfApprovalTests
{
    [IntegrationTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/unpublish-requests")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task Unpublish_SeparateHumanApproval_ClearsPublishedPointerAndLineage()
    {
        var (_, version) = await SeedPublishedAppAsync();
        using var proposer = CreateBearerClient(CreateToken("withdraw-proposer", administrator: true));
        using var approver = CreateBearerClient(CreateToken("withdraw-reviewer", administrator: true));
        var proposalId = await ProposeUnpublishAsync(proposer, version);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        Assert.Equal(version.VersionId, (await store.GetPointersAsync(version.ItemId))!.PublishedVersionId);
        using var selfApproval = await proposer.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);
        using var approval = await approver.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.Equal("Succeeded", (await GetProposalAsync(approver, proposalId)).GetProperty("status").GetString());
        Assert.Null((await store.GetPointersAsync(version.ItemId))!.PublishedVersionId);
        var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
        Assert.Empty(await index.ListAppServicesAsync(version.ItemId));
        Assert.Empty(await index.ListServiceAppsAsync(version.PackageKey));
        Assert.Null(await store.GetActivePublicationRequestByRouteAsync("/apps/" + version.PackageKey));
    }

    [IntegrationTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/unpublish-requests")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task Unpublish_RepublishedAfterProposal_RefusesStaleApproval()
    {
        var (draft, version) = await SeedPublishedAppAsync();
        using var proposer = CreateBearerClient(CreateToken("stale-withdraw-proposer", administrator: true));
        using var approver = CreateBearerClient(CreateToken("stale-withdraw-reviewer", administrator: true));
        var proposalId = await ProposeUnpublishAsync(proposer, version);
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        var newer = await store.CreateVersionAsync(draft, "new publication", "owner");
        await PublishAppAsync(store, newer);
        using var approval = await approver.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.Equal("Failed", (await GetProposalAsync(approver, proposalId)).GetProperty("status").GetString());
        Assert.Equal(newer.VersionId, (await store.GetPointersAsync(version.ItemId))!.PublishedVersionId);
        var index = Assert.IsAssignableFrom<IStudioPublicationIndex>(store);
        Assert.Equal(newer.VersionId, Assert.Single(await index.ListAppServicesAsync(version.ItemId)).VersionId);
    }

    private static async Task<string> ProposeUnpublishAsync(HttpClient proposer, StudioContentVersion version)
    {
        using var response = await proposer.PostAsJsonAsync($"/api/v1/studio/content-items/{version.ItemId:D}/unpublish-requests",
            new { expectedPublishedVersionId = version.VersionId, reason = "Withdraw publication" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("proposalId").GetString()!;
    }

    private async Task<(StudioPackageDraft Draft, StudioContentVersion Version)> SeedPublishedAppAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStudioPackageStore>();
        var key = "withdraw-" + Guid.NewGuid().ToString("N");
        var draft = await store.CreateDraftAsync(new()
        {
            DraftId = Guid.NewGuid(), ItemId = Guid.NewGuid(), PackageKey = key, Family = StudioPackageFamily.App,
            TenantId = Tenant, OwnerId = "owner", Generation = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Envelope = new()
            {
                Family = StudioPackageFamily.App, SchemaVersion = "1.0", Dependencies = [new() { Kind = "service", Ref = key }]
            }
        });
        var version = await store.CreateVersionAsync(draft, null, "owner");
        await PublishAppAsync(store, version);
        return (draft, version);
    }

    private static Task<StudioPublicationRequest> PublishAppAsync(IStudioPackageStore store, StudioContentVersion version)
        => store.CreatePublicationRequestAsync(new()
        {
            RequestId = Guid.NewGuid(), ItemId = version.ItemId, VersionId = version.VersionId,
            Status = StudioPublicationRequestStatus.Accepted, RequestedBy = "owner", CreatedAt = DateTimeOffset.UtcNow,
            Intent = new() { Route = "/apps/" + version.PackageKey, Visibility = "public" }
        }, version.VersionId);
}
