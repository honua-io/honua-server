// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Operations.Policy;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderReplayAuthorizationFilterTests
{
    [UnitTheory]
    [InlineData("valid", true)]
    [InlineData("forged", false)]
    [InlineData("replayed", false)]
    [InlineData("expired", false)]
    [InlineData("wrong-operation", false)]
    [InlineData("wrong-tenant", false)]
    [InlineData("wrong-resolved-tenant", false)]
    [InlineData("wrong-instance", false)]
    [InlineData("wrong-credential", false)]
    [InlineData("ordinary", false)]
    [InlineData("wrong-folder-path", false)]
    [InlineData("wrong-proposal-lineage", false)]
    [InlineData("changed-body", false)]
    [InlineData("changed-etag", false)]
    [InlineData("changed-namespace", false)]
    [InlineData("missing-payload", false)]
    [InlineData("completed", false)]
    [InlineData("changed-plan", false)]
    public async Task InvokeAsync_ReplayBoundary_DelegatesOnlySealedExecutingFolderOperation(string scenario, bool delegated)
    {
        var proposal = Proposal();
        if (scenario == "wrong-proposal-lineage") proposal = proposal with { ProposalId = "another" };
        if (scenario == "wrong-operation") proposal = proposal with { OperationId = "folder.delete" };
        if (scenario == "wrong-tenant") proposal = proposal with { TenantId = "another" };
        if (scenario == "completed") proposal = proposal with { Status = OperationProposalStatus.Succeeded };
        if (scenario == "missing-payload") proposal = proposal with { Plan = proposal.Plan with { ExecutionPayload = null } };
        if (scenario == "changed-plan") proposal = proposal with { SealedPlanHash = "invalid" };
        using var services = Services(proposal, scenario == "wrong-resolved-tenant" ? "another" : "public");
        var context = Context(scenario);
        context.RequestServices = services;
        var original = context.User;
        await InvokeWithLineageAsync(context, scenario, invocation =>
        {
            invocation.HttpContext.User.IsInRole(OperationPolicyBuiltInRules.FullAdministratorRole).Should().Be(delegated);
            return ValueTask.FromResult<object?>(null);
        });
        context.User.Should().BeSameAs(original, "delegation must end with the folder invocation");
    }

    [UnitTest]
    public async Task InvokeAsync_HandlerThrows_RestoresTransportPrincipal()
    {
        using var services = Services(Proposal(), "public");
        var context = Context("valid");
        context.RequestServices = services;
        var original = context.User;
        var invoke = () => InvokeWithLineageAsync(context, "valid", _ => throw new InvalidOperationException("handler"));
        await invoke.Should().ThrowAsync<InvalidOperationException>();
        context.User.Should().BeSameAs(original);
    }

    [UnitTest]
    public async Task InvokeAsync_DelegatedAdminGrant_CannotAuthorizeDeniedService()
    {
        using var services = Services(Proposal(), "public");
        var resolver = services.GetRequiredService<IPermissionResolver>();
        resolver.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), "restricted", null,
            AuthorizationOperation.Admin, true, Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<string>>().Contains(OperationPolicyBuiltInRules.FullAdministratorRole)
                ? PermissionDecision.Allow(new PermissionGrant { Service = "restricted", Layer = "restricted", Operation = "Admin" })
                : PermissionDecision.NoMatch());
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Name = "restricted" },
            AccessPolicy = new AccessPolicy { AllowedRoles = ["service-owner"] }
        };
        var context = Context("valid");
        context.RequestServices = services;
        await InvokeWithLineageAsync(context, "valid", async invocation =>
        {
            invocation.HttpContext.User.IsInRole(OperationPolicyBuiltInRules.FullAdministratorRole).Should().BeTrue();
            (await AccessPolicyHelpers.RequireServiceAccessAsync(context, service, AuthorizationOperation.Admin)).Should().BeNull();
            var denied = await FolderEndpoints.RequireServiceAccessAsync(context, service);
            denied.Should().BeAssignableTo<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            context.User.IsInRole(OperationPolicyBuiltInRules.FullAdministratorRole).Should().BeTrue();
            return null;
        });
    }

    private static ServiceProvider Services(OperationProposal proposal, string tenantId)
    {
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(proposal);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        return new ServiceCollection().AddSingleton(store).AddSingleton(tenant)
            .AddSingleton<IOperationApprovalReplayVerifier, OperationApprovalReplayVerifier>()
            .AddSingleton(Substitute.For<IPermissionResolver>())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions())).BuildServiceProvider();
    }

    private static OperationProposal Proposal()
    {
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.update",
            TenantId = "public",
            Parameters = new() { ["id"] = "folder", ["name"] = "Approved", ["If-Match"] = "\"revision\"" }
        };
        var plan = new OperationProposalPlan
        {
            Summary = "update folder",
            ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
        };
        return new OperationProposal
        {
            ProposalId = "proposal",
            OperationId = "folder.update",
            TenantId = "public",
            Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.Executing,
            Plan = plan,
            SealedPlanHash = OperationApprovalPlanSeal.Compute(plan),
            Audit = new OperationAuditInfo { OperationInstanceId = "instance" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static DefaultHttpContext Context(string scenario)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "PATCH";
        context.Request.Path = scenario == "wrong-folder-path" ? "/api/v1/admin/folders/another" : "/api/v1/admin/folders/folder";
        context.Request.Headers.IfMatch = scenario == "changed-etag" ? "\"another\"" : "\"revision\"";
        if (scenario == "changed-namespace") context.Request.QueryString = new QueryString("?namespace=other");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, scenario == "ordinary" ? "scoped-admin-key" : AdminApiKeyPermission.ApprovedOperationRole),
            new Claim(AdminApiKeyPermission.PermissionClaimType,
                AdminApiKeyPermission.CreateApprovedOperationGrant(scenario == "wrong-credential" ? "DELETE" : "PATCH", context.Request.Path.Value!)),
            new Claim(AdminApiKeyPermission.PermissionClaimType,
                FolderReplayRequest.ProposalGrant("proposal")),
            new Claim(AdminApiKeyPermission.ApprovedOperationTenantClaim, "public")], "ApiKey"));
        return context;
    }

    private static async Task InvokeWithLineageAsync(HttpContext context, string scenario, EndpointFilterDelegate next)
    {
        var clock = new ReplayClock();
        var lineage = new OperationLineageAttestationStore(clock);
        var token = lineage.Issue(new OperationPolicyContext { ProposalId = scenario == "wrong-proposal-lineage" ? "another" : "proposal", OperationInstanceId = scenario == "wrong-instance" ? "another" : "instance" });
        if (scenario == "replayed") lineage.TryConsume(token, out _);
        if (scenario == "expired") clock.Now = clock.Now.AddMinutes(6);
        context.Request.Headers[OperationLineageHeaders.Attestation] = scenario == "forged" ? "invalid" : token;
        context.Request.Headers[OperationLineageHeaders.ProposalId] = "forged-proposal";
        if (scenario == "ordinary") context.Request.Headers.Remove(OperationLineageHeaders.Attestation);
        var middleware = new OperationLineageAttestationMiddleware(async current =>
        {
            await new FolderReplayAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(current, "folder",
                new FolderRequest { Name = scenario == "changed-body" ? "Unapproved" : "Approved" }, current), next);
        });
        await middleware.InvokeAsync(context, lineage);
    }

    private sealed class ReplayClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
