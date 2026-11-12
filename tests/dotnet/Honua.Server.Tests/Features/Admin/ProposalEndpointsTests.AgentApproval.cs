// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    private static readonly string[] AgentKeyPermissions = ["admin:read", "admin:write", "agent"];
    private static readonly string[] HumanKeyPermissions = ["admin:read", "admin:write"];
    private const string ConsoleGuidance = "ask one to make this change in Console";

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}/share/access")]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    [Endpoint("DELETE /api/v1/admin/rls-policies/{id}")]
    public async Task ShareVisibilityAndRowPolicies_AgentKey_CannotWidenAccess()
    {
        using var agent = await CreateKeyClientAsync("agent-sharing", AgentKeyPermissions);
        var id = Guid.NewGuid();

        var share = await agent.PutAsJsonAsync($"/api/v1/console/content/{id}/share/access", new { accessTier = "public-indexed" });
        var widen = await agent.PatchAsJsonAsync($"/api/v1/console/content/{id}", new { visibility = "public" });
        var narrow = await agent.PatchAsJsonAsync($"/api/v1/console/content/{id}", new { visibility = "personal" });
        var unmask = await agent.DeleteAsync($"/api/v1/admin/rls-policies/{id}");

        await AssertAgentApprovalRefusalAsync(share, ConsoleGuidance, AgentApprovalFilter.AccessGrantsRef);
        await AssertAgentApprovalRefusalAsync(widen, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        (await narrow.Content.ReadAsStringAsync()).Should().NotContain("urn:honua:approval-required");
        await AssertAgentApprovalRefusalAsync(unmask, ConsoleGuidance, AgentApprovalFilter.AccessGrantsRef);
    }

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

        // The service does not exist, so reaching the handler (past the agent filter) yields 404.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata")]
    public async Task UpdateLayerMetadata_AgentKeyChangingAccess_RequiresHumanApproval()
    {
        using var agent = await CreateKeyClientAsync("agent-layer-access", AgentKeyPermissions);

        var changesAccess = await agent.PutAsJsonAsync(
            "/api/v1/admin/services/roads/layers/0/metadata", new { accessPolicy = new { allowAnonymous = true } });
        var leavesAccess = await agent.PutAsJsonAsync(
            "/api/v1/admin/services/roads/layers/0/metadata", new { description = "Road centrelines" });

        await AssertAgentApprovalRefusalAsync(changesAccess, ConsoleGuidance, AgentApprovalFilter.AccessPolicyRef);
        leavesAccess.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, await leavesAccess.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{layerId}/enabled")]
    [Endpoint("PUT /api/v1/admin/roles/{id}/permissions")]
    public async Task EnableLayerAndRoleGrants_AgentKey_RequireHumanApproval()
    {
        using var agent = await CreateKeyClientAsync("agent-enable-and-grants", AgentKeyPermissions);

        var enable = await agent.PutAsJsonAsync("/api/v1/admin/connections/c1/layers/1/enabled", new { enabled = true });
        var grants = await agent.PutAsJsonAsync(
            $"/api/v1/admin/roles/{Guid.NewGuid()}/permissions", new { permissions = new[] { new { service = "roads", layer = "*", operation = "read" } } });

        await AssertAgentApprovalRefusalAsync(enable, ConsoleGuidance, AgentApprovalFilter.LayerStateRef);
        await AssertAgentApprovalRefusalAsync(grants, ConsoleGuidance, AgentApprovalFilter.AccessGrantsRef);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    [Endpoint("PUT /api/v1/admin/oauth-scopes")]
    public async Task ContentPublicationAndOAuthScopes_AgentKey_AreRefused()
    {
        using var agent = await CreateKeyClientAsync("agent-content-and-scopes", ["admin", AgentKeyPermissions[2]]);

        var publication = await agent.PostAsJsonAsync("/api/v1/console/publications", new { routeSlug = "roads", title = "Roads" });
        var scope = await agent.PutAsJsonAsync("/api/v1/admin/oauth-scopes", new { scope = "roads.read" });

        await AssertAgentApprovalRefusalAsync(publication, ConsoleGuidance, AgentApprovalFilter.AccessGrantsRef);
        await AssertAgentApprovalRefusalAsync(scope, ConsoleGuidance, AgentApprovalFilter.AccessGrantsRef);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task SubmitOtherGatedOperation_AgentKey_IsRefusedWhateverTheApprovalSettings()
    {
        using var agent = await CreateKeyClientAsync("agent-fields-operation", AgentKeyPermissions);
        var request = new OperationInvokeRequest
        {
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["layerId"] = "1" },
        };

        var response = await agent.PostAsJsonAsync("/api/v1/operations/admin.layer.fields.set/submit", request, JsonOptions);

        await AssertAgentApprovalRefusalAsync(response, ConsoleGuidance, AgentApprovalFilter.OperationRef);
        _executor.ExecutionCount.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_ApprovedOperationReplay_IsNotRefusedForApproval()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        const string path = "/api/v1/admin/connections/c1/layers";
        var issued = await store.CreateAsync(
            "approved-operation:agent-publish-proposal",
            AdminApiKeyPermission.CreateApprovedOperationGrants("POST", path, "public"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "agent-requester",
            CancellationToken.None);
        using var replay = CreateApiKeyClient(issued.Key);

        var response = await replay.PostAsJsonAsync(path, new { schema = "public", table = "roads", layerName = "Roads" });

        // Connection c1 does not exist: getting past the filter and the approval gate ends in the handler.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("urn:honua:approval-required");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, body);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, body);
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

    private static async Task AssertAgentApprovalRefusalAsync(
        HttpResponseMessage response, string guidance = "Console Approvals", string? policyRef = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("type").GetString().Should().Be("urn:honua:approval-required");
        problem.RootElement.GetProperty("detail").GetString().Should().Contain(guidance);
        if (policyRef is not null)
        {
            problem.RootElement.GetProperty("policyRef").GetString().Should().Be(policyRef);
        }
    }
}
