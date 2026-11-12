// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    private static readonly string[] AgentKeyPermissions = ["admin:read", "admin:write", "agent"];
    private static readonly string[] HumanKeyPermissions = ["admin:read", "admin:write"];

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_AgentKey_RequiresHumanApproval()
    {
        using var agent = await CreateKeyClientAsync("agent-access-policy", AgentKeyPermissions);

        var response = await agent.PutAsJsonAsync("/api/v1/admin/services/roads/access-policy", new { allowAnonymous = true });

        await AssertAgentApprovalRefusalAsync(response);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task UpdateAccessPolicy_HumanAdministratorKey_IsNotGated()
    {
        using var human = await CreateKeyClientAsync("human-access-policy", HumanKeyPermissions);

        var response = await human.PutAsJsonAsync("/api/v1/admin/services/roads/access-policy", new { allowAnonymous = true });

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_AgentKey_IsNotExemptAsAdministrator()
    {
        using var agent = await CreateKeyClientAsync("agent-layer-publish", AgentKeyPermissions);

        var response = await agent.PostAsJsonAsync("/api/v1/admin/connections/c1/layers", new { schema = "public", table = "roads", layerName = "Roads" });

        await AssertAgentApprovalRefusalAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task SubmitAccessPolicyOperation_AgentKey_FilesProposalForHumanApproval()
    {
        using var agent = await CreateKeyClientAsync("agent-access-policy-operation", AgentKeyPermissions);
        var request = new OperationInvokeRequest
        {
            ServiceName = "roads",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["serviceName"] = "roads", ["allowAnonymous"] = "true" },
        };

        var response = await agent.PostAsJsonAsync(
            "/api/v1/operations/admin.services.access-policy.set/submit", request, JsonOptions);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var handle = document.RootElement.GetProperty("data");
        handle.GetProperty("status").ToString().Should().BeOneOf(
            nameof(OperationHandleStatus.RequiresApproval), "requiresApproval", ((int)OperationHandleStatus.RequiresApproval).ToString(System.Globalization.CultureInfo.InvariantCulture));
        handle.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
        _executor.ExecutionCount.Should().Be(0);
    }

    private async Task<HttpClient> CreateKeyClientAsync(string name, IReadOnlyList<string> permissions)
        => CreateApiKeyClient((await CreateApiKeyAsync(name, permissions)).Key);

    private static async Task AssertAgentApprovalRefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("type").GetString().Should().Be("urn:honua:approval-required");
        problem.RootElement.GetProperty("detail").GetString().Should().Contain("Console Approvals");
    }
}
