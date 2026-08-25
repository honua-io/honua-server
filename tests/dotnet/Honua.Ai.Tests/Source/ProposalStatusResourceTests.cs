// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Geoprocessing;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class ProposalStatusResourceTests
{
    [UnitTest]
    public async Task ReadAsync_RetainedProposerUsesBoundResourceAndOperation()
    {
        var store = Substitute.For<IOperationProposalStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        store.GetAsync("proposal-1", Arg.Any<CancellationToken>()).Returns(CreateProposal("stable-subject"));
        var context = CreateContext("stable-subject", store, jobService);
        var resource = new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance);

        var result = await resource.ReadAsync(
            context,
            "honua://proposals/proposal-1",
            CancellationToken.None);

        result.Contents.Should().ContainSingle();
        await jobService.Received(1).EnsureCallerAuthorizedAsync(
            context.User,
            OperatorResourceType.Deployment,
            OperatorOperation.Publish,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ReadAsync_AuthorizedReviewerCanPollProposal()
    {
        var store = Substitute.For<IOperationProposalStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        store.GetAsync("proposal-1", Arg.Any<CancellationToken>()).Returns(CreateProposal("original-subject"));
        var context = CreateContext("different-subject", store, jobService);
        var resource = new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance);

        var result = await resource.ReadAsync(
            context,
            "honua://proposals/proposal-1",
            CancellationToken.None);

        result.Contents.Should().ContainSingle();
        await jobService.Received(1).EnsureCallerAuthorizedAsync(
            context.User,
            OperatorResourceType.Deployment,
            OperatorOperation.Read,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ReadAsync_TenantlessModeCanPollTenantlessProposal()
    {
        var store = Substitute.For<IOperationProposalStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        store.GetAsync("proposal-1", Arg.Any<CancellationToken>())
            .Returns(CreateProposal("stable-subject", OperationAuthorityContext.Tenantless));
        var context = CreateContext(
            "stable-subject",
            store,
            jobService,
            tenant: null,
            multiTenancyEnabled: false);
        var resource = new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance);

        var result = await resource.ReadAsync(
            context,
            "honua://proposals/proposal-1",
            CancellationToken.None);

        result.Contents.Should().ContainSingle();
        await jobService.Received(1).EnsureCallerAuthorizedAsync(
            context.User,
            OperatorResourceType.Deployment,
            OperatorOperation.Publish,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://issuer.example", "other-tenant")]
    public async Task ReadAsync_DifferentTenantCannotPollProposal(
        string issuer,
        string tenant)
    {
        var store = Substitute.For<IOperationProposalStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        store.GetAsync("proposal-1", Arg.Any<CancellationToken>()).Returns(CreateProposal("stable-subject"));
        var context = CreateContext("stable-subject", store, jobService, issuer, tenant);
        var resource = new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance);

        var act = () => resource.ReadAsync(
            context,
            "honua://proposals/proposal-1",
            CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await jobService.DidNotReceiveWithAnyArgs().EnsureCallerAuthorizedAsync(
            default!, default, default, default);
    }

    private static DefaultHttpContext CreateContext(
        string subject,
        IOperationProposalStore store,
        IGeoprocessingJobService jobService,
        string issuer = "https://issuer.example",
        string? tenant = "tenant-1",
        bool multiTenancyEnabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MultiTenancy:Enabled"] = multiTenancyEnabled.ToString(),
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(jobService)
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ITenantContext>(new TestTenantContext(tenant))
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(ClaimTypes.Name, "Mutable Display Name"),
                new Claim("iss", issuer),
            ], "Bearer")),
        };
    }

    private static OperationProposal CreateProposal(
        string actor,
        string effectiveTenant = "tenant-1") => new()
        {
            ProposalId = "proposal-1",
            Kind = OperationClass.Deploy,
            Status = OperationProposalStatus.AwaitingApproval,
            CreatedAt = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
            Authority = new OperationAuthorityContext
            {
                Issuer = "https://issuer.example",
                Actor = actor,
                Scheme = "Bearer",
                EffectiveTenant = effectiveTenant,
                ScopeGoverned = true,
                ResourceType = OperatorResourceType.Deployment,
                Operation = OperatorOperation.Publish,
                ResourceId = "target-1",
            },
        };

    private sealed class TestTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;

        public TenantContextSource Source => TenantContextSource.Claim;

        public bool RequireTenantId(out string resolvedTenantId, out string? reason)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                resolvedTenantId = string.Empty;
                reason = "Tenant context is unavailable.";
                return false;
            }

            resolvedTenantId = tenantId;
            reason = null;
            return true;
        }
    }
}
