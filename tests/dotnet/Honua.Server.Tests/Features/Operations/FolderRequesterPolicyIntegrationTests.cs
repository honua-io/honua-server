// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
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
        return saved;
    }
}
