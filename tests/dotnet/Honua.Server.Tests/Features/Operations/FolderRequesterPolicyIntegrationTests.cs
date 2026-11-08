// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.Server.Tests.Features.Admin;
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
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var body = JsonDocument.Parse(payload);
        body.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Denied", payload);
        body.RootElement.GetProperty("data").GetProperty("reason").GetString().Should().Be("The requester does not have access to every affected service.");
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
            (await _proposals.GetAsync(proposalId))!.ResolutionReason.Should().Be(FolderApprovalAuthorization.FreshProposalRequired);
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
        (await _proposals.GetAsync(proposalId))!.ResolutionReason.Should().Be(FolderApprovalAuthorization.FreshProposalRequired);
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
        await WithRequesterContextAsync(payload, async context =>
        {
            var result = await _fixture.Services.GetRequiredService<IOperationGateway>().CreateApprovalProposalAsync(original.Audit.OperationInstanceId!, new()
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
            result.Message.Should().Be("The requester does not have access to every affected service.");
            result.ProposalId.Should().BeNull();
        });
        (await SnapshotAsync()).Etag.Should().Be(restricted.Etag);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [InlineData("roles")]
    [InlineData("inactive")]
    [InlineData("removed")]
    [InlineData("entitlement")]
    [InlineData("gateway-unavailable")]
    [InlineData("endpoint-unavailable")]
    public async Task FolderCreate_RequesterPolicyChangesOrBecomesUnavailable_PreservesRefusalReason(string change)
    {
        if (change == "entitlement") _licensing.Current = new TestLicenseEntitlementService(HonuaEdition.Enterprise);
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create", TenantId = "public", SchemaName = _fixture.CurrentSchema,
            Parameters = new() { ["name"] = "SubjectFolder", ["If-Match"] = before.Etag }
        };
        await WithRequesterContextAsync(payload, async context =>
        {
            var result = await _fixture.Services.GetRequiredService<IOperationGateway>().CreateApprovalProposalAsync("subject-" + Guid.NewGuid().ToString("N"), new()
            {
                OperationId = payload.OperationId, Kind = OperationClass.AdminConfigChange, TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                Plan = new OperationProposalPlan
                {
                    Summary = "Create subject folder",
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, result.Message);
            var proposal = (await _proposals.GetAsync(result.ProposalId!))!;
            proposal.Plan.FolderAuthorization!.SubjectId.Should().Be("folder-requester");
            proposal.Plan.FolderAuthorization.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddDays(31));
            JsonSerializer.Serialize(proposal.Plan, Honua.ControlPlane.OperationProposalJsonContext.Default.OperationProposalPlan)
                .Should().NotContain("original-membership");
            if (change == "gateway-unavailable") _memberships.ReadsBeforeOutage = 0;
            else if (change == "endpoint-unavailable") _memberships.ReadsBeforeOutage = 1;
            else if (change == "entitlement") _licensing.Current = new TestLicenseEntitlementService(HonuaEdition.Pro);
            else _memberships.Current = change switch
            {
                "roles" => new PrincipalMembership(true, ["viewer"]),
                "inactive" => new PrincipalMembership(false, ["admin"]),
                _ => null
            };
            await ApproveAsync(result.ProposalId!);
            var refused = (await _proposals.GetAsync(result.ProposalId!))!;
            refused.Status.Should().Be(OperationProposalStatus.Failed);
            if (change == "gateway-unavailable") refused.ResolutionReason.Should().Be("Execution failed (ServiceUnavailableException).");
            else if (change == "endpoint-unavailable") refused.ResolutionReason.Should().StartWith("Admin API returned 503");
            else refused.ResolutionReason.Should().Be(FolderApprovalAuthorization.FreshProposalRequired);
        }, "folder-requester");
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderCreate_IdempotencyCollision_RequiresNewKeyForActiveAndTerminalProposal(bool terminal)
    {
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create", TenantId = "public", SchemaName = _fixture.CurrentSchema,
            Parameters = new() { ["name"] = "First", ["If-Match"] = before.Etag }
        };
        await WithRequesterContextAsync(payload, async context =>
        {
            var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();
            var request = new OperationGatewayRequest
            {
                OperationId = payload.OperationId, Kind = OperationClass.AdminConfigChange, TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                IdempotencyKey = "folder-" + Guid.NewGuid().ToString("N"),
                Plan = new OperationProposalPlan
                {
                    Summary = "Create folder",
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            };
            var first = await gateway.CreateApprovalProposalAsync("first-" + Guid.NewGuid().ToString("N"), request);
            first.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, first.Message);
            if (terminal)
            {
                var proposal = (await _proposals.GetAsync(first.ProposalId!))!;
                (await _proposals.TrySetAsync(proposal with { Status = OperationProposalStatus.Rejected })).Should().BeTrue();
            }
            var active = await _proposals.ListActiveAsync();
            if (terminal) active.Should().NotContain(proposal => proposal.ProposalId == first.ProposalId);
            else active.Should().Contain(proposal => proposal.ProposalId == first.ProposalId);
            payload.Parameters["name"] = "Second";
            var second = await gateway.CreateApprovalProposalAsync("second-" + Guid.NewGuid().ToString("N"), request with
            {
                Plan = request.Plan with
                {
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            second.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
            second.Message.Should().Be(FolderApprovalAuthorization.NewIdempotencyKeyRequired);
            second.ProposalId.Should().BeNull();
        });
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    private async Task WithRequesterContextAsync(AdminApiOperationApprovalPayload payload, Func<HttpContext, Task> action, string? subject = null)
    {
        using var scope = _fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SchemaContext>().CurrentSchema = payload.SchemaName;
        scope.ServiceProvider.GetRequiredService<RequestTenantContext>().Set(payload.TenantId, TenantContextSource.Claim);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var scheme = AuthenticationExtensions.ApiKeyScheme;
        if (subject is null)
            context.Request.Headers["X-API-Key"] = _proposer.DefaultRequestHeaders.GetValues("X-API-Key").Single();
        else
        {
            scheme = RlsClaimsTestAuthHandler.SchemeName;
            context.Request.Headers[RlsClaimsTestAuthHandler.UserHeader] = subject;
            context.Request.Headers[RlsClaimsTestAuthHandler.RolesHeader] = "admin";
        }
        var authenticated = await context.AuthenticateAsync(scheme);
        authenticated.Succeeded.Should().BeTrue();
        context.User = authenticated.Principal!;
        context.Features.Set<IAuthenticateResultFeature>(new RequesterAuthenticationFeature { AuthenticateResult = authenticated });
        var accessor = _fixture.Services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        try { accessor.HttpContext = context; await action(context); }
        finally { accessor.HttpContext = previous; }
    }

    private sealed class RequesterAuthenticationFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    private sealed class FolderRequesterLicense : ILicenseEntitlementService
    {
        public TestLicenseEntitlementService Current { get; set; } = new(HonuaEdition.Pro);
        public LicenseSnapshot GetSnapshot() => Current.GetSnapshot();
        public LicenseEntitlementDecision CheckEntitlement(string entitlementKey) => Current.CheckEntitlement(entitlementKey);
    }

    private sealed class FolderRequesterMembership : IPrincipalMembershipSource
    {
        public PrincipalMembership? Current { get; set; } = new(true, ["admin", "original-membership"]);
        public int? ReadsBeforeOutage { get; set; }

        public Task<PrincipalMembership?> ResolveMembershipAsync(string principalId, CancellationToken cancellationToken = default)
        {
            if (ReadsBeforeOutage is { } remaining)
            {
                if (remaining == 0) return Task.FromException<PrincipalMembership?>(new ServiceUnavailableException("Membership service unavailable."));
                ReadsBeforeOutage = remaining - 1;
            }
            return Task.FromResult(principalId == "folder-requester" ? Current : null);
        }
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
