// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    private const string AuthorityUrl = "/api/v1/admin/agent-authority";

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/agent-authority")]
    [Endpoint("GET /api/v1/admin/agent-authority")]
    [Endpoint("DELETE /api/v1/admin/agent-authority")]
    public async Task AgentAuthority_HumanCanPersistReadAndRevokeCeiling()
    {
        var ceiling = NewAuthorityCeiling();
        var query = $"?principalId={ceiling.PrincipalId}&tenantId=public";
        using var set = await _client.PutAsJsonAsync(AuthorityUrl, ceiling);
        set.StatusCode.Should().Be(HttpStatusCode.NoContent, await set.Content.ReadAsStringAsync());
        var stored = await _client.GetFromJsonAsync<AgentAuthorityCeiling>(AuthorityUrl + query);
        stored.Should().BeEquivalentTo(ceiling);
        using var deleted = await _client.DeleteAsync(AuthorityUrl + query);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var missing = await _client.GetAsync(AuthorityUrl + query);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/agent-authority")]
    [Endpoint("DELETE /api/v1/admin/agent-authority")]
    public async Task AgentAuthority_AgentCannotChangeOrDeleteAnExistingCeiling()
    {
        var ceiling = NewAuthorityCeiling();
        using var seeded = await _client.PutAsJsonAsync(AuthorityUrl, ceiling);
        seeded.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var agent = await CreateKeyClientAsync("authority-agent", AgentKeyPermissions);
        using var changed = await agent.PutAsJsonAsync(AuthorityUrl, ceiling with { Enabled = false });
        await AssertAgentApprovalRefusalAsync(changed, "make this change in Console", "operator.agent.access-grants");
        using var removed = await agent.DeleteAsync(AuthorityUrl + $"?principalId={ceiling.PrincipalId}&tenantId=public");
        await AssertAgentApprovalRefusalAsync(removed, "make this change in Console", "operator.agent.access-grants");
        var stored = await _client.GetFromJsonAsync<AgentAuthorityCeiling>(
            AuthorityUrl + $"?principalId={ceiling.PrincipalId}&tenantId=public");
        stored.Should().BeEquivalentTo(ceiling);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/agent-authority")]
    public async Task AgentAuthority_InvalidCeilingIsRejected()
    {
        using var response = await _client.PutAsJsonAsync(AuthorityUrl,
            NewAuthorityCeiling() with { AllowedOperationIds = ["*"] });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static AgentAuthorityCeiling NewAuthorityCeiling() => new()
    {
        TenantId = "public", PrincipalId = $"agent-{Guid.NewGuid():N}",
        AllowedOperationIds = ["service.publish"],
        MaximumBlastRadius = OperationBlastRadiusClass.ServiceScope,
        MaximumSideEffect = OperationSideEffectClass.CreatesMetadata,
    };
}
