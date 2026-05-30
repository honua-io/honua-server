// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

[Protocol(Honua.TestKit.Constants.ProtocolNames.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Streaming)]
public sealed class CollaborationSessionEndpointsTests
{
    private const string AdminPassword = "collaboration-session-test-key";

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    public async Task Join_WithAuthorizedSavedMapCapability_ReturnsSessionSnapshot()
    {
        using var factory = CreateFactory(allowJoin: true);
        using var client = CreateClient(factory);
        using var response = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/sessions/join",
            JsonBody(new CollaborationJoinRequest { DisplayName = "Ada", ClientInstanceId = "browser-1" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("participant").GetProperty("displayName").GetString().Should().Be("Ada");
        data.GetProperty("snapshot").GetProperty("mapId").GetString().Should().Be("saved-map:ops");
        data.GetProperty("snapshot").GetProperty("participants").GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join")]
    public async Task Join_WithoutSavedMapCapability_ReturnsUnauthorized()
    {
        using var factory = CreateFactory(allowJoin: false);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/sessions/join",
            JsonBody(new CollaborationJoinRequest { DisplayName = "Ada" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool allowJoin)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword
                    });
                });

                if (allowJoin)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ISavedMapCollaborationAuthorizer>();
                        services.AddSingleton<ISavedMapCollaborationAuthorizer, AllowSavedMapCollaborationAuthorizer>();
                    });
                }
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }

    private static StringContent JsonBody(CollaborationJoinRequest request)
    {
        var json = JsonSerializer.Serialize(
            request,
            CollaborationSessionJsonContext.Default.CollaborationJoinRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class AllowSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SavedMapCollaborationAuthorizationResult.Allow());
    }
}
