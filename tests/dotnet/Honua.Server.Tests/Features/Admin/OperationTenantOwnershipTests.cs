// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using InstanceStore = Honua.Core.Features.Operations.Abstractions.IOperationInstanceStore;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
public sealed class OperationTenantOwnershipTests
{
    [Theory]
    [InlineData("HandleGetProposal")]
    [InlineData("HandleApproveProposal")]
    [InlineData("HandleRejectProposal")]
    public async Task ProposalEndpoint_OtherTenant_ReturnsNotFoundWithoutDecision(string handler)
    {
        var proposal = Proposal("tenant-a");
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync("proposal-a", Arg.Any<CancellationToken>()).Returns(proposal);
        var gateway = Substitute.For<IOperationGateway>();
        gateway.ApplyApprovedProposalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(proposal);
        gateway.RejectProposalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(proposal);
        using var services = Services("tenant-b", store);
        var context = Context(services);
        var result = await Invoke(typeof(ProposalEndpoints), handler, context, store, gateway);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(404);
        await gateway.DidNotReceiveWithAnyArgs().ApplyApprovedProposalAsync(default!, default!, default);
        await gateway.DidNotReceiveWithAnyArgs().RejectProposalAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("tenant-a", "admin", 1)]
    [InlineData("tenant-b", "admin", 0)]
    [InlineData("tenant-b", "platform_admin", 1)]
    public async Task ListProposals_FiltersByTenantUnlessExplicitCrossTenantRole(string tenant, string role, int count)
    {
        var store = Substitute.For<IOperationProposalStore>();
        store.ListActiveAsync(Arg.Any<Honua.Core.Features.Guardrails.Domain.OperationClass?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Proposal("tenant-a") });
        using var services = Services(tenant, store);
        var result = await Invoke(typeof(ProposalEndpoints), "HandleListProposals", Context(services, role), store);
        var response = (ProposalListResponse)((IValueHttpResult)result).Value!;
        response.Proposals.Should().HaveCount(count);
    }

    [Theory]
    [InlineData("tenant-a", "admin", 200)]
    [InlineData("tenant-b", "admin", 404)]
    [InlineData("tenant-b", "platform_admin", 200)]
    public async Task HandleRead_ChecksTenantOwnership(string tenant, string role, int status)
    {
        var store = Substitute.For<InstanceStore>();
        store.GetAsync("handle-a", Arg.Any<CancellationToken>()).Returns(JsonSerializer.Deserialize<OperationHandle>("""
            {"OperationInstanceId":"handle-a","OperationId":"admin.test","TenantId":"tenant-a","CorrelationId":"corr-a","Status":0,"CreatedAt":"2026-09-04T00:00:00Z","UpdatedAt":"2026-09-04T00:00:00Z"}
            """)!);
        using var services = Services(tenant, Substitute.For<IOperationProposalStore>());
        var result = await Invoke(typeof(OperationsEndpoints), "HandleGetHandleStatus", Context(services, role), instanceStore: store);
        (((IStatusCodeHttpResult)result).StatusCode ?? 200).Should().Be(status);
    }

    [Theory]
    [InlineData("tenant-a", "admin", 200)]
    [InlineData("", "admin", 404)]
    [InlineData("", "platform_admin", 200)]
    public async Task ProposalRead_LegacyOwnershipRequiresPlatformAuthority(string owner, string role, int status)
    {
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync("proposal-a", Arg.Any<CancellationToken>()).Returns(Proposal(owner));
        using var services = Services("tenant-a", store);
        var result = await Invoke(typeof(ProposalEndpoints), "HandleGetProposal", Context(services, role), store);
        (((IStatusCodeHttpResult)result).StatusCode ?? 200).Should().Be(status);
    }

    [Fact]
    public async Task EnvelopeAcceptance_PreservesTenantAndSeparatesIdempotentRequests()
    {
        var store = Substitute.For<InstanceStore>();
        store.TryCreateAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>()).Returns(true);
        var audit = Substitute.For<Honua.Core.Features.AuditLog.Abstractions.IAuditLog>();
        audit.RecordAsync(Arg.Any<Honua.Core.Features.AuditLog.Abstractions.AuditEvent>(), Arg.Any<CancellationToken>()).Returns("audit-a");
        var factory = new Honua.Core.Features.Operations.Services.OperationEnvelopeFactory(store, audit, TimeProvider.System);
        var context = new OperationPolicyContext { TenantId = "tenant-a", IdempotencyKey = "same-key" };
        var first = await factory.CreateAcceptedAsync("admin.test", context);
        var second = await factory.CreateAcceptedAsync("admin.test", context with { TenantId = "tenant-b" });
        var serialized = JsonSerializer.SerializeToElement(first);
        serialized.TryGetProperty("TenantId", out var tenant).Should().BeTrue();
        tenant.GetString().Should().Be("tenant-a");
        first.OperationInstanceId.Should().NotBe(second.OperationInstanceId);
    }

    private static OperationProposal Proposal(string tenant) => JsonSerializer.Deserialize<OperationProposal>(
        $$"""{"ProposalId":"proposal-a","TenantId":"{{tenant}}","Kind":2,"Status":1,"RequestedBy":"other-actor","CreatedAt":"2026-09-04T00:00:00Z","UpdatedAt":"2026-09-04T00:00:00Z"}""")!;

    private static ServiceProvider Services(string tenantId, IOperationProposalStore store)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return new ServiceCollection().AddLogging().AddSingleton(tenant).AddSingleton(store)
            .AddSingleton(auth).AddSingleton(Options.Create(new TenantContextOptions())).BuildServiceProvider();
    }

    private static DefaultHttpContext Context(IServiceProvider services, string role = "admin") => new()
    {
        RequestServices = services,
        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "reviewer"), new Claim(ClaimTypes.Role, role),
        }, "Test")),
    };

    private static async Task<IResult> Invoke(Type type, string handler, HttpContext context,
        IOperationProposalStore? store = null, IOperationGateway? gateway = null, InstanceStore? instanceStore = null)
    {
        var method = type.GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)!;
        var permission = Substitute.For<IPermissionResolver>();
        permission.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<Honua.Core.Features.Authorization.Domain.AuthorizationOperation>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Honua.Core.Features.Authorization.Domain.PermissionDecision.NoMatch());
        var args = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "context" => (object)context,
            "id" => "proposal-a",
            "handleId" => "handle-a",
            "proposalStore" => store,
            "gateway" => gateway,
            "instanceStore" => instanceStore,
            "permissionResolver" => permission,
            "request" => new RejectProposalRequest { Reason = "declined" },
            "cancellationToken" => CancellationToken.None,
            _ => null,
        }).ToArray();
        return await (Task<IResult>)method.Invoke(null, args)!;
    }
}
