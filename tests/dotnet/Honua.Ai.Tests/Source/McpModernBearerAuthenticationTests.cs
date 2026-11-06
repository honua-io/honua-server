// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using static Honua.Server.Tests.Features.Protocols.Mcp.McpBearerAuthenticationTests;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpModernBearerAuthenticationTests
{
    [IntegrationTheory]
    [InlineData("modern", "modern")]
    [InlineData("modern", "http")]
    [InlineData("http", "modern")]
    [InlineData("legacy", "modern")]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/studio/package-families")]
    [InterfaceOperation(TestProtocols.Mcp, "server/discover")]
    public async Task ReplayProtectionEnabled_ModernBearerReuse_PreservesExistingRefusals(string firstSurface, string secondSurface)
    {
        var fixture = CreateReplayProtectedFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            var token = CreateToken("modern-replay-owner", additionalClaims:
            [
                new Claim("tid", "tenant-a"),
                new Claim("roles", "admin"),
                new Claim("scope", "honua.mcp.full")
            ]);
            using var first = BuildReplayProbe(firstSurface, token);
            using var admitted = await client.SendAsync(first);
            admitted.StatusCode.Should().Be(HttpStatusCode.OK);

            using var second = BuildReplayProbe(secondSurface, token);
            if (admitted.Headers.TryGetValues("Mcp-Session-Id", out var session))
            {
                second.Headers.Add("Mcp-Session-Id", session);
            }

            using var refused = await client.SendAsync(second);
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    public async Task Post_InvalidModernOriginWithInvalidBearer_Returns403BeforeAuthentication()
    {
        var fixture = CreateReplayProtectedFixture().ConfigureServices(services =>
            services.PostConfigure<CorsOptions>(options => options.AddPolicy(HttpCorsPolicyNames.Production,
                policy => policy.WithOrigins("https://approved.example"))));
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var request = BuildModernDiscovery("invalid-token");
            request.Headers.Add("Origin", "https://unapproved.example");
            request.Content = new StringContent("{broken", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static HttpRequestMessage BuildReplayProbe(string surface, string bearer) => surface switch
    {
        "http" => BuildPackageFamiliesRequest(bearer),
        "legacy" => BuildInitialize(bearer),
        _ => BuildModernDiscovery(bearer)
    };

    private static HttpRequestMessage BuildModernDiscovery(string bearer)
    {
        var request = BuildRpc(
            """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
            sessionId: null, bearer: bearer);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

}
