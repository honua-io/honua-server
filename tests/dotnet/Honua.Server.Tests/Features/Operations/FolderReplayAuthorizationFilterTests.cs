// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
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
    [InlineData("completed", false)]
    [InlineData("changed-plan", false)]
    public async Task InvokeAsync_ReplayBoundary_DelegatesOnlySealedExecutingFolderOperation(string scenario, bool delegated)
    {
        var proposal = Proposal();
        if (scenario == "wrong-operation") proposal = proposal with { OperationId = "folder.delete" };
        if (scenario == "wrong-tenant") proposal = proposal with { TenantId = "another" };
        if (scenario == "completed") proposal = proposal with { Status = OperationProposalStatus.Succeeded };
        if (scenario == "changed-plan") proposal = proposal with { SealedPlanHash = "invalid" };
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync("proposal", Arg.Any<CancellationToken>()).Returns(proposal);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(scenario == "wrong-resolved-tenant" ? "another" : "public");
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(tenant)
            .AddSingleton<IOperationApprovalReplayVerifier, OperationApprovalReplayVerifier>().BuildServiceProvider();
        var context = Context(scenario);
        context.RequestServices = services;
        var original = context.User;
        await InvokeWithLineageAsync(context, scenario, invocation =>
        {
            invocation.HttpContext.User.IsInRole("admin").Should().Be(delegated);
            return ValueTask.FromResult<object?>(null);
        });
        context.User.Should().BeSameAs(original, "delegation must end with the folder invocation");
    }

    [UnitTest]
    public async Task InvokeAsync_HandlerThrows_RestoresTransportPrincipal()
    {
        var proposalStore = Substitute.For<IOperationProposalStore>();
        proposalStore.GetAsync("proposal", Arg.Any<CancellationToken>()).Returns(Proposal());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("public");
        using var services = new ServiceCollection().AddSingleton(proposalStore).AddSingleton(tenant)
            .AddSingleton<IOperationApprovalReplayVerifier, OperationApprovalReplayVerifier>().BuildServiceProvider();
        var context = Context("valid");
        context.RequestServices = services;
        var original = context.User;
        var invoke = () => InvokeWithLineageAsync(context, "valid", _ => throw new InvalidOperationException("handler"));
        await invoke.Should().ThrowAsync<InvalidOperationException>();
        context.User.Should().BeSameAs(original);
    }

    private static OperationProposal Proposal()
    {
        var plan = new OperationProposalPlan { Summary = "create folder" };
        return new OperationProposal
        {
            ProposalId = "proposal", OperationId = "folder.create", TenantId = "public",
            Kind = OperationClass.AdminConfigChange, Status = OperationProposalStatus.Executing,
            Plan = plan, SealedPlanHash = OperationApprovalPlanSeal.Compute(plan),
            Audit = new OperationAuditInfo { OperationInstanceId = "instance" },
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static DefaultHttpContext Context(string scenario)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, scenario == "ordinary" ? "scoped-admin-key" : AdminApiKeyPermission.ApprovedOperationRole),
            new Claim(AdminApiKeyPermission.PermissionClaimType,
                AdminApiKeyPermission.CreateApprovedOperationGrant(scenario == "wrong-credential" ? "DELETE" : "POST", "/api/v1/admin/folders")),
            new Claim(AdminApiKeyPermission.ApprovedOperationTenantClaim, "public")], "ApiKey"));
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/admin/folders";
        return context;
    }

    private static async Task InvokeWithLineageAsync(HttpContext context, string scenario, EndpointFilterDelegate next)
    {
        var clock = new ReplayClock();
        var lineage = new OperationLineageAttestationStore(clock);
        var token = lineage.Issue(new OperationPolicyContext { ProposalId = "proposal", OperationInstanceId = scenario == "wrong-instance" ? "another" : "instance" });
        if (scenario == "replayed") lineage.TryConsume(token, out _);
        if (scenario == "expired") clock.Now = clock.Now.AddMinutes(6);
        context.Request.Headers[OperationLineageHeaders.Attestation] = scenario == "forged" ? "invalid" : token;
        context.Request.Headers[OperationLineageHeaders.ProposalId] = "forged-proposal";
        if (scenario == "ordinary") context.Request.Headers.Remove(OperationLineageHeaders.Attestation);
        var middleware = new OperationLineageAttestationMiddleware(async current =>
        {
            await new FolderReplayAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(current), next);
        });
        await middleware.InvokeAsync(context, lineage);
    }

    private sealed class ReplayClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
