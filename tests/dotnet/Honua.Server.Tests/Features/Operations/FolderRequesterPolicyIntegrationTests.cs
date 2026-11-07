// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Features.Operations;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [InlineData("service.move")]
    [InlineData("folder.update")]
    public async Task SubmitFolderOperation_RequesterDeniedByOneAffectedFacet_DoesNotAcceptProposal(string operationId)
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var created = await SnapshotAsync();
        var folderId = created.Graph.Folders.Single().Metadata.Id;
        if (operationId == "folder.update")
            await ApplyAsync("service.move", new() { ["folderId"] = folderId, ["If-Match"] = created.Etag });

        var restricted = await RestrictLastServiceFacetAsync();
        var activeBefore = (await _proposals.ListActiveAsync()).Select(proposal => proposal.ProposalId).ToHashSet();
        var parameters = new Dictionary<string, string?> { ["If-Match"] = restricted.Etag };
        if (operationId == "service.move") parameters["folderId"] = folderId;
        else
        {
            parameters["id"] = folderId;
            parameters["title"] = "Changed display title";
        }

        using var response = await _proposer.PostAsJsonAsync($"/api/v1/operations/{operationId}/submit",
            new { serviceName = "test", parameters });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Forbidden);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(payload);
            body.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Denied", payload);
        }
        (await _proposals.ListActiveAsync()).Should().OnlyContain(proposal => activeBefore.Contains(proposal.ProposalId),
            "submission must check every affected facet before accepting an approval proposal");
        (await SnapshotAsync()).Etag.Should().Be(restricted.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ServiceMove_Submission_PersistsSealedAuthorizationForEveryFacet()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var proposalId = await ProposeAsync("service.move", new()
        {
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id, ["If-Match"] = before.Etag
        });
        var proposal = (await _proposals.GetAsync(proposalId))!;
        proposal.Plan.FolderAuthorization.Should().NotBeNull();
        var evidence = proposal.Plan.FolderAuthorization!;
        evidence.RequesterId.Should().Be(proposal.RequestedBy);
        evidence.OperationId.Should().Be("service.move");
        evidence.SnapshotEtag.Should().Be(before.Etag);
        evidence.PolicyRevision.Should().HaveLength(64);
        evidence.ServiceIds.Should().BeEquivalentTo(before.Graph.Services.Where(service => service.Metadata.Name == "test")
            .Select(service => service.Metadata.Id));
        evidence.ServiceIds.Should().HaveCountGreaterThan(1);
        OperationApprovalPlanSeal.Compute(proposal.Plan).Should().Be(proposal.SealedPlanHash);
        var wire = JsonSerializer.Serialize(proposal.Plan, Honua.ControlPlane.OperationProposalJsonContext.Default.OperationProposalPlan);
        wire.Should().NotContain("service-owner").And.NotContain("\"roles\"");
        await ApproveAsync(proposalId);
        (await _proposals.GetAsync(proposalId))!.Status.Should().Be(OperationProposalStatus.Succeeded);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderCreate_RolePolicyChangesWithoutCatalogChange_RequiresFreshProposal()
    {
        var before = await SnapshotAsync();
        var proposalId = await ProposeAsync("folder.create", new() { ["name"] = "Pending", ["If-Match"] = before.Etag });
        using var scope = _fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleStore>();
        var created = await roles.CreateRoleAsync(new RoleDefinition { Name = "folder-policy-" + Guid.NewGuid().ToString("N") });
        try
        {
            await roles.SetPermissionsAsync(created.RoleId, [new PermissionGrant { Service = "test", Layer = "*", Operation = "admin" }]);
            (await SnapshotAsync()).Etag.Should().Be(before.Etag, "role policy changes do not change the metadata ETag");
            await ApproveAsync(proposalId);
            (await _proposals.GetAsync(proposalId))!.Status.Should().Be(OperationProposalStatus.Failed);
            (await SnapshotAsync()).Etag.Should().Be(before.Etag);
            (await SnapshotAsync()).Graph.Folders.Should().NotContain(folder => folder.Metadata.Name == "Pending");
        }
        finally { await roles.DeleteRoleAsync(created.RoleId); }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task FolderCreate_RequesterRestrictionsChange_RequiresFreshProposal()
    {
        var before = await SnapshotAsync();
        var proposalId = await ProposeAsync("folder.create", new() { ["name"] = "Pending", ["If-Match"] = before.Etag });
        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var requester = await keys.ValidateAsync(_proposer.DefaultRequestHeaders.GetValues("X-API-Key").Single(), CancellationToken.None);
        requester.Should().NotBeNull();
        (await keys.UpdateRestrictionsAsync(requester!.Record.Id,
            new AdminApiKeyRestrictions(null, ["192.0.2.0/24"]), CancellationToken.None)).Should().NotBeNull();
        await ApproveAsync(proposalId);
        (await _proposals.GetAsync(proposalId))!.Status.Should().Be(OperationProposalStatus.Failed);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task ServiceMove_SuppliedPlanCannotBorrowRequesterAuthorization()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        var pending = await ProposeAsync("service.move", new()
        {
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id, ["If-Match"] = before.Etag
        });
        var original = (await _proposals.GetAsync(pending))!;
        var restricted = await RestrictLastServiceFacetAsync();
        var payload = JsonSerializer.Deserialize(original.Plan.ExecutionPayload!,
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)!;
        payload.Parameters["If-Match"] = restricted.Etag;
        using var scope = _fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SchemaContext>().CurrentSchema = payload.SchemaName;
        scope.ServiceProvider.GetRequiredService<RequestTenantContext>().Set(original.TenantId, TenantContextSource.Claim);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers["X-API-Key"] = _proposer.DefaultRequestHeaders.GetValues("X-API-Key").Single();
        context.User = (await context.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme)).Principal!;
        var accessor = _fixture.Services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        try
        {
            accessor.HttpContext = context;
            var result = await _fixture.Services.GetRequiredService<IOperationGateway>().CreateApprovalProposalAsync("untrusted-plan", new()
            {
                OperationId = "service.move", Kind = OperationClass.AdminConfigChange,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                TenantId = original.TenantId,
                Plan = original.Plan with
                {
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            result.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
            result.ProposalId.Should().BeNull();
        }
        finally { accessor.HttpContext = previous; }
        (await SnapshotAsync()).Etag.Should().Be(restricted.Etag);
    }

    private async Task<MetadataV2GraphSnapshot> RestrictLastServiceFacetAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var before = await store.GetCurrentAsync();
        var facet = before.Graph.Services.Last(service => service.Metadata.Name == "test");
        var restricted = before.Graph with
        {
            Services = before.Graph.Services.Select(service => service.Metadata.Id == facet.Metadata.Id
                ? service with { AccessPolicy = new AccessPolicy { AllowedRoles = ["service-owner"], AllowedWriteRoles = ["service-owner"] } }
                : service).ToArray()
        };
        var saved = await store.SaveAsync(restricted, before.Etag);
        saved.Etag.Should().NotBe(before.Etag);
        var policyContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        policyContext.Request.Method = HttpMethods.Put;
        policyContext.Request.Path = "/api/v1/admin/services/test/folder";
        policyContext.Request.Headers["X-API-Key"] = _proposer.DefaultRequestHeaders.GetValues("X-API-Key").Single();
        var authenticated = await policyContext.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
        authenticated.Succeeded.Should().BeTrue();
        policyContext.User = authenticated.Principal!;
        var deniedFacet = saved.Graph.Services.Single(service => service.Metadata.Id == facet.Metadata.Id);
        MetadataV2TenantVisibility.IsVisibleToTenant(deniedFacet, null).Should().BeTrue();
        (await FolderEndpoints.RequireServiceAccessAsync(policyContext, deniedFacet)).Should().NotBeNull(
            "the real authenticated requester must fail the shared service-policy check before testing proposal admission");
        return saved;
    }
}
