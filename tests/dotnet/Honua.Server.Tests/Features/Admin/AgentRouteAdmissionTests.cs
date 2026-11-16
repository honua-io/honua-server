// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

// An agent changes admin and Console state through the operations API or MCP; a direct call to a
// changing admin or Console route is refused unless the route decides for itself.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AgentRouteAdmissionTests : IAsyncLifetime
{
    private const string AdminPassword = "agent-route-human-key";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });
    private HttpClient _human = null!;
    private HttpClient _agent = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _human = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        var key = await _fixture.Services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "route-agent", ["admin", AgentPrincipal.AgentGrant], null, "test", CancellationToken.None);
        _agent = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key.Key));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/connections/{id}")]
    public async Task AgentAdministrator_ChangingAnAdminRouteDirectly_IsRefusedWithApprovalGuidance()
    {
        var path = $"/api/v1/admin/connections/{Guid.NewGuid()}";

        using var agent = await _agent.DeleteAsync(path);
        using var human = await _human.DeleteAsync(path);

        agent.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await agent.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("policyRef").GetString().Should().Be(AgentRouteAdmissionMiddleware.PolicyRef);
        problem.RootElement.GetProperty("detail").GetString().Should().Contain("/api/v1/operations/");
        human.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "a person keeps direct access");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections")]
    public async Task AgentAdministrator_ReadingAnAdminRoute_IsNotRefused()
    {
        using var response = await _agent.GetAsync("/api/v1/admin/connections");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/actions/check")]
    public async Task AgentAdministrator_RouteThatDecidesForItself_IsNotRefusedByTheRouteRule()
    {
        using var content = new StringContent("""{"targets":[]}""", Encoding.UTF8, "application/json");

        using var response = await _agent.PostAsync("/api/v1/console/actions/check", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the handler, not the route rule, answers");
    }
}
