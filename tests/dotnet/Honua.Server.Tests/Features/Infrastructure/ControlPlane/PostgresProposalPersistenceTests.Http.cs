// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class PostgresProposalPersistenceTests
{
    [IntegrationTest]
    public async Task TrySetAsync_RetentionHint_DoesNotExpireApprovalHistory()
    {
        var proposal = Proposal() with
        {
            Status = OperationProposalStatus.Rejected,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-60),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-40),
            ResolvedBy = "reviewer",
            ResolutionReason = "Historical decision"
        };
        Assert.True(await Store.TryCreateAsync(proposal, TimeSpan.FromTicks(1)));
        var restored = Assert.IsType<OperationProposal>(await Store.GetAsync(proposal.ProposalId));
        Assert.Equal(proposal.ResolutionReason, restored.ResolutionReason);
        Assert.True(await Store.TrySetAsync(restored with { ResolutionReason = "Retained decision" }, TimeSpan.FromTicks(1)));
        await _fixture.RestartHostAsync();
        restored = Assert.IsType<OperationProposal>(await Store.GetAsync(proposal.ProposalId));
        Assert.Equal("Retained decision", restored.ResolutionReason);
        Assert.Equal(1, restored.Version);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposals_WithoutRedis_ReadsPersistedProposal()
    {
        var proposal = Proposal();
        Assert.True(await Store.TryCreateAsync(proposal));
        using var client = _fixture.CreateAdminClient();
        using var list = await client.GetAsync("/api/v1/admin/proposals");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(proposal.ProposalId, await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var detail = await client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(proposal.ProposalId, document.RootElement.GetProperty("proposalId").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithoutRedis_ProposalsAvailableAndJobsUnavailable()
    {
        using var client = _fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capabilities = document.RootElement.GetProperty("capabilities").EnumerateArray().ToArray();
        var proposals = Assert.Single(capabilities, item => item.GetProperty("id").GetString() == "operations.proposals");
        Assert.True(proposals.GetProperty("supported").GetBoolean());
        Assert.True(proposals.GetProperty("available").GetBoolean());
        var jobs = Assert.Single(capabilities, item => item.GetProperty("id").GetString() == "jobs.runner");
        Assert.False(jobs.GetProperty("available").GetBoolean());
    }
}
