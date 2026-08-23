// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// HTTP-level REST/MCP owner-policy parity proof for #3412. The generic MCP
/// operator gate is explicitly admitted so the test isolates the loaded-owner
/// decision both protocols share through <c>IStudioAuthorizationService</c>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMcpOwnershipParityIntegrationTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
        })
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
            services.RemoveAll<IOperatorAuthorizationEvaluator>();
            services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    [Endpoint("POST /mcp tools/call honua_studio_update_draft")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    public async Task RestAndMcp_ApplyTheSameOwnerAndCrossUserDenialRules()
    {
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var aliceKey = await apiKeyStore.CreateAsync(
            "mcp-owner-alice", ["studio:enduser"], null, null, CancellationToken.None);
        var bobKey = await apiKeyStore.CreateAsync(
            "mcp-owner-bob", ["studio:enduser"], null, null, CancellationToken.None);
        var aliceOwnerId = aliceKey.Record.Id.ToString("D");
        var bobOwnerId = bobKey.Record.Id.ToString("D");
        using var aliceClient = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", aliceKey.Key));
        using var bobClient = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", bobKey.Key));

        // A non-admin MCP caller cannot assign the draft to someone else.
        var created = await CallToolAsync(
            aliceClient,
            "honua_studio_create_draft",
            $$"""
              {"packageKey":"owner-parity-map","family":"map","schemaVersion":"1.0","ownerId":"{{bobOwnerId}}"}
              """);
        var draftId = created.GetProperty("structuredContent").GetProperty("draftId").GetGuid();
        created.GetProperty("structuredContent").GetProperty("ownerId").GetString()
            .Should().Be(aliceOwnerId);

        // The owner succeeds through both adapters.
        using var aliceRestResponse = await aliceClient.GetAsync(
            $"/api/v1/studio/package-drafts/{draftId:D}");
        aliceRestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var aliceMcpRead = await CallToolAsync(
            aliceClient,
            "honua_studio_get_draft",
            $$"""{"draftId":"{{draftId:D}}"}""");
        var aliceReadFailed = aliceMcpRead.TryGetProperty("isError", out var aliceReadError)
            && aliceReadError.GetBoolean();
        aliceReadFailed.Should().BeFalse();

        // The same guessed, existing cross-user id is governed by the same
        // stable Studio denial code over REST and MCP.
        using var bobRestResponse = await bobClient.GetAsync(
            $"/api/v1/studio/package-drafts/{draftId:D}");
        bobRestResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var restProblem = JsonDocument.Parse(await bobRestResponse.Content.ReadAsStringAsync());
        restProblem.RootElement.GetProperty("code").GetString()
            .Should().Be(StudioAuthorizationService.CrossUserDeniedCode);

        var bobMcpRead = await CallToolAsync(
            bobClient,
            "honua_studio_get_draft",
            $$"""{"draftId":"{{draftId:D}}"}""");
        AssertCrossUserToolDenial(bobMcpRead);

        var bobMcpUpdate = await CallToolAsync(
            bobClient,
            "honua_studio_update_draft",
            $$"""
              {"draftId":"{{draftId:D}}","generation":1,"packageKey":"stolen-map","schemaVersion":"1.0","ownerId":"{{bobOwnerId}}"}
              """);
        AssertCrossUserToolDenial(bobMcpUpdate);

        // Alice may update, but cannot transfer ownership to Bob.
        var aliceMcpUpdate = await CallToolAsync(
            aliceClient,
            "honua_studio_update_draft",
            $$"""
              {"draftId":"{{draftId:D}}","generation":1,"packageKey":"owner-parity-map-v2","schemaVersion":"1.0","ownerId":"{{bobOwnerId}}"}
              """);
        aliceMcpUpdate.GetProperty("structuredContent").GetProperty("ownerId").GetString()
            .Should().Be(aliceOwnerId);
    }

    private static void AssertCrossUserToolDenial(JsonElement result)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var error = result.GetProperty("structuredContent");
        error.GetProperty("code").GetString().Should().Be("permission_denied");
        error.GetProperty("studioAuthorizationCode").GetString()
            .Should().Be(StudioAuthorizationService.CrossUserDeniedCode);
    }

    private static async Task<JsonElement> CallToolAsync(
        HttpClient client,
        string toolName,
        string argumentsJson)
    {
        var body = $$"""
            {"jsonrpc":"2.0","id":"{{toolName}}","method":"tools/call","params":{"name":"{{toolName}}","arguments":{{argumentsJson}} } }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) },
            },
        };
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hasProtocolError = document.RootElement.TryGetProperty("error", out var protocolError);
        var protocolErrorText = hasProtocolError ? protocolError.GetRawText() : "(none)";
        hasProtocolError.Should().BeFalse(
            $"tool '{toolName}' should return a tool result, not JSON-RPC error: {protocolErrorText}");
        return document.RootElement.GetProperty("result").Clone();
    }

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("parity fixture"));
    }
}
