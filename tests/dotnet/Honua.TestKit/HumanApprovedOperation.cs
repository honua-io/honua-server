// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.TestKit;

internal static class HumanApprovedOperation
{
    internal static async Task<HttpClient> CreateProposerClientAsync(WebAppFixture fixture)
    {
        var keys = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var key = await keys.CreateAsync($"approval-proposer-{Guid.NewGuid():N}", ["admin:write"],
            DateTimeOffset.UtcNow.AddMinutes(30), "approval-contract-test", CancellationToken.None);
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key.Key);
        return client;
    }

    internal static async Task<JsonElement> ApproveAndReadHandleAsync(WebAppFixture fixture, string proposalId)
    {
        using var human = await EmulatedHumanApprovalSession.CreateClientAsync(fixture.Services, () => fixture.CreateClient());
        using var review = await human.GetAsync($"/api/v1/admin/proposals/{proposalId}/approval-review");
        review.IsSuccessStatusCode.Should().BeTrue(await review.Content.ReadAsStringAsync());
        using var snapshot = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        var capability = snapshot.RootElement.GetProperty("review").GetProperty("capability").GetString();
        using var decision = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/proposals/{proposalId}/approve");
        decision.Headers.Add("X-Honua-Approval-Capability", capability);
        using var approved = await human.SendAsync(decision);
        var body = await approved.Content.ReadAsStringAsync();
        approved.IsSuccessStatusCode.Should().BeTrue(body);
        using var approval = JsonDocument.Parse(body);
        approval.RootElement.GetProperty("status").GetString().Should().Be("Succeeded", body);
        var operationId = approval.RootElement.GetProperty("executionOperationId").GetString();
        operationId.Should().NotBeNullOrWhiteSpace();
        using var readback = await human.GetAsync($"/api/v1/operations/handles/{operationId}");
        readback.EnsureSuccessStatusCode();
        using var handle = JsonDocument.Parse(await readback.Content.ReadAsStringAsync());
        return handle.RootElement.GetProperty("data").Clone();
    }
}
