// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using IOperationInstanceStore = Honua.Core.Features.Operations.Abstractions.IOperationInstanceStore;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    public static IEnumerable<object[]> NonHumanApprovalCases =>
        Enum.GetValues<OperationClass>().SelectMany(kind => new[]
        {
            new object[] { kind, "admin:write" },
            new object[] { kind, "admin:approve" }
        });

    public static IEnumerable<object[]> ProposalKinds =>
        Enum.GetValues<OperationClass>().Select(kind => new object[] { kind });

    [IntegrationTheory]
    [MemberData(nameof(ProposalKinds))]
    public async Task ApplyApprovedProposalAsync_ActorContextWithoutHumanProof_DoesNotReleaseAuthority(OperationClass kind)
    {
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer", kind: kind);
        var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();
        var approval = new OperationProposalApprovalContext
        {
            ApprovedBy = "different-person",
            TenantId = "public",
        };

        var apply = () => gateway.ApplyApprovedProposalAsync(proposal.ProposalId, approval);

        await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*human*");
        (await _proposalStore.GetAsync(proposal.ProposalId)).Should().BeEquivalentTo(proposal);
        _executor.ExecutionCount.Should().Be(0);
        _notifier.ResolvedCount.Should().Be(0);
    }

    [IntegrationTheory]
    [MemberData(nameof(NonHumanApprovalCases))]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("GET /api/v1/admin/proposals/{id}/approval-review")]
    public async Task ApproveProposal_ApiKeyWithDecisionGrant_RequiresHumanSession(
        OperationClass kind, string grant)
    {
        var key = await CreateApiKeyAsync("noninteractive-decision", ["admin:read", grant]);
        var proposal = await SeedProposalAsync(requestedBy: "agent:proposer", kind: kind);
        using var client = CreateApiKeyClient(key.Key);
        await AssertNonHumanDecisionDeniedAsync(client, proposal);
    }

    [IntegrationTheory]
    [InlineData("admin:write")]
    [InlineData("admin:approve")]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    [Endpoint("GET /api/v1/admin/proposals/{id}/approval-review")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_RegisteredOperatorGatedDescriptors_RefusesApiKeyForEveryOperation(string grant)
    {
        var catalog = _fixture.Services.GetRequiredService<Honua.Core.Features.Operations.Abstractions.IOperationCatalog>();
        var operations = (await catalog.GetSnapshotAsync()).Operations
            .Where(operation => operation.ApprovalModel == OperationApprovalModel.OperatorGate).ToArray();
        operations.Should().NotBeEmpty().And.Contain(operation => operation.OperationId.EndsWith("folder.create", StringComparison.Ordinal));
        var key = await CreateApiKeyAsync("registered-operation-decision", ["admin:read", grant]);
        using var client = CreateApiKeyClient(key.Key);
        foreach (var operation in operations)
        {
            var proposal = await SeedProposalAsync(requestedBy: "agent:proposer", operationId: operation.OperationId);
            await AssertNonHumanDecisionDeniedAsync(client, proposal);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}/approval-review")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_ConcurrentVersionWrite_RequiresFreshReviewWithoutSecurityDenial()
    {
        var proposal = await SeedProposalAsync();
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);
        var review = await human.OpenReviewAsync(proposal.ProposalId);
        _proposalStore.AdvanceVersionOnNextClaim = true;

        using var response = await human.SubmitReviewAsync(proposal.ProposalId, review);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().ContainEquivalentOf("review");
        var pending = (await _proposalStore.GetAsync(proposal.ProposalId))!;
        pending.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        pending.HumanApproval.Should().BeNull();
        _executor.ExecutionCount.Should().Be(0);
        using var scope = _fixture.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<Honua.Core.Features.AuditLog.Abstractions.IAuditLogReader>()
            .ListAsync(new Honua.Core.Features.AuditLog.Abstractions.AuditLogFilter
            {
                ResourceId = proposal.ProposalId,
                PageSize = 20,
            });
        audits.Items.Should().NotContain(item => item.Action == "operation.approval_denied");
        using var retry = await human.ApproveAsync(proposal.ProposalId);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        _executor.ExecutionCount.Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}/approval-review")]
    public async Task OpenApprovalReview_ProjectsTheAuthoritativeTenantForThePrivateConsoleBinding()
    {
        var proposal = await SeedProposalAsync();
        using var human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identityProvider);

        var review = await human.OpenReviewAsync(proposal.ProposalId);

        review.TenantId.Should().Be(proposal.TenantId);
    }

    private async Task AssertNonHumanDecisionDeniedAsync(HttpClient client, OperationProposal proposal)
    {
        using var read = await client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsync($"/api/v1/admin/proposals/{proposal.ProposalId}/approve", null);
        using var review = await client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}/approval-review");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        review.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await review.Content.ReadAsStringAsync()).Should().NotContain("capability\":");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("human").And.Contain("Console").And.Contain("MCP");
        (await _proposalStore.GetAsync(proposal.ProposalId)).Should().BeEquivalentTo(proposal);
        _executor.ExecutionCount.Should().Be(0);
        _notifier.ResolvedCount.Should().Be(0);
    }

}
