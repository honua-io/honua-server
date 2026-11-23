// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposal_PublishFailedOnMovedCatalog_RequiresFreshProposal()
    {
        var proposal = await SeedProposalAsync(
            status: OperationProposalStatus.Failed,
            kind: OperationClass.ServicePublish,
            operationId: "service.publish");
        await _proposalStore.TrySetAsync(proposal with { ResolutionReason = LayerPublicationBaseChanged.ProposalReason });

        using var response = await _client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("requiresReproposal").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("approvalRemediation").GetString()
            .Should().Be(LayerPublicationBaseChanged.ProposalReason);
        document.RootElement.GetProperty("resolutionReason").GetString()
            .Should().Be(LayerPublicationBaseChanged.ProposalReason);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task GetProposal_PublishFailedForAnotherReason_DoesNotAskForFreshProposal()
    {
        var proposal = await SeedProposalAsync(
            status: OperationProposalStatus.Failed,
            kind: OperationClass.ServicePublish,
            operationId: "service.publish");
        await _proposalStore.TrySetAsync(proposal with { ResolutionReason = "Layer already exists for table." });

        using var response = await _client.GetAsync($"/api/v1/admin/proposals/{proposal.ProposalId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("requiresReproposal").GetBoolean().Should().BeFalse();
        document.RootElement.TryGetProperty("approvalRemediation", out _).Should().BeFalse();
    }
}
