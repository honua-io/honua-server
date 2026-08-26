// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.StudioAi;

/// <summary>
/// Endpoint-level tests for the Studio AI proxy (honua-server#3000/#3303): Studio lifecycle
/// authorization on both routes, the capabilities shape, and — the audit-record requirement
/// (REQ-002) — that a chat call reaching a known, configured (but unreachable) provider still
/// produces exactly one audit record with the expected action/resource/outcome. No live provider
/// is called anywhere in this file: the "provider" here is an unreachable loopback port, so the
/// HTTP call fails fast with a connection error that the adapter turns into a normal <c>error</c>
/// SSE event.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class StudioAiProxyEndpointsTests : IAsyncLifetime
{
    private const string ProviderName = "test-openai";
    private const string Issuer = "https://studio-idp.example.com";
    private const string Audience = "honua-studio-client-id";
    private const string SigningKey = "studio-ai-proxy-test-signing-key-at-least-32-characters";
    private readonly CapturingAuditLog _audit = new();
    private readonly WebAppFixture _fixture;

    public StudioAiProxyEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IAuditLog>(_audit);
            })
            .ConfigureWebHost(builder =>
            {
                ConfigureHost(builder, endUserAuthorizationEnabled: false);
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    public async Task GetCapabilities_Anonymous_Returns401()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/api/v1/studio/ai/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    public async Task GetCapabilities_Admin_ReturnsConfiguredProvider()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/studio/ai/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("enabled").GetBoolean().Should().BeTrue();
        data.GetProperty("defaultProvider").GetString().Should().Be(ProviderName);

        var providers = data.GetProperty("providers");
        providers.GetArrayLength().Should().Be(1);
        var provider = providers[0];
        provider.GetProperty("provider").GetString().Should().Be(ProviderName);
        provider.GetProperty("kind").GetString().Should().Be("openai");
        provider.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        provider.GetProperty("configured").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task StudioAiProxy_FlagOff_AuthenticatedNonAdminBearerReturns403()
    {
        using var client = CreateBearerClient(_fixture, CreateToken("studio-user-disabled"));

        using var capabilitiesResponse = await client.GetAsync("/api/v1/studio/ai/capabilities");
        using var chatResponse = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            model = "unapproved-model",
            messages = new[] { new { role = "user", content = "hi" } }
        });

        capabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        chatResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertEndUserModeDisabledProblemAsync(capabilitiesResponse);
        await AssertEndUserModeDisabledProblemAsync(chatResponse);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "initialize")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task StudioAiProxy_FlagOn_SameNonAdminBearerReachesProxyAndMcpDiscovery()
    {
        var audit = new CapturingAuditLog();
        await using var fixture = await CreateEndUserFixtureAsync(audit);
        var token = CreateToken("studio-user-enabled", tenantId: "studio-tenant");
        using var client = CreateBearerClient(fixture, token);

        using var capabilitiesResponse = await client.GetAsync("/api/v1/studio/ai/capabilities");
        capabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var chatResponse = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });
        chatResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        chatResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        (await chatResponse.Content.ReadAsStringAsync()).Should().Contain("event: error");

        using var initializeResponse = await client.SendAsync(BuildMcpRequest(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"studio-auth-test","version":"1.0.0"}}}
            """));
        initializeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds).Should().BeTrue();
        var sessionId = sessionIds!.Single();

        using var toolsResponse = await client.SendAsync(BuildMcpRequest(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId));
        toolsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var toolsDocument = JsonDocument.Parse(await toolsResponse.Content.ReadAsStringAsync());
        toolsDocument.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        toolsDocument.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength().Should().BeGreaterThan(0);

        var chatAudit = audit.Recorded.Should().ContainSingle(e => e.Action == "studio_ai.chat").Subject;
        chatAudit.Actor.Should().Be(
            "bearer:subject:https%3A%2F%2Fstudio-idp.example.com:studio-user-enabled");
        chatAudit.ActorType.Should().Be(AuditActorType.UserId);
        chatAudit.Details.Should().Contain("\"model\":\"test-model\"");
        chatAudit.Details.Should().NotContain("unapproved-model",
            "non-admin callers are pinned to the operator-configured model");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task StudioAiProxy_FlagOn_ClientCredentialsBearerReturns403()
    {
        var audit = new CapturingAuditLog();
        await using var fixture = await CreateEndUserFixtureAsync(audit);
        var token = CreateToken("studio-machine", isClientCredentials: true);
        using var client = CreateBearerClient(fixture, token);

        using var capabilitiesResponse = await client.GetAsync("/api/v1/studio/ai/capabilities");
        using var chatResponse = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });

        capabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        chatResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertInteractivePrincipalRequiredProblemAsync(capabilitiesResponse);
        await AssertInteractivePrincipalRequiredProblemAsync(chatResponse);
        audit.Recorded.Should().HaveCount(2);
        audit.Recorded.Should().OnlyContain(auditEvent =>
            auditEvent.Action == "studio.lifecycle" &&
            auditEvent.Outcome == AuditOutcome.Denied &&
            auditEvent.Details == "{\"code\":\"studio_authorization/interactive_principal_required\"}");
        audit.Recorded.Should().NotContain(auditEvent => auditEvent.Action == "studio_ai.chat",
            "a client-credentials bearer must be denied before any model-provider call begins");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task StudioAiProxy_FlagOn_NonAdminScopedApiKeyReturns403()
    {
        var audit = new CapturingAuditLog();
        await using var fixture = await CreateEndUserFixtureAsync(audit);
        var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var scopedKey = await apiKeyStore.CreateAsync(
            "studio-ai-unrelated-key",
            ["studio:enduser"],
            null,
            null,
            CancellationToken.None);
        using var client = fixture.CreateClient(
            options => options.DefaultRequestHeaders.Add("X-API-Key", scopedKey.Key));

        using var capabilitiesResponse = await client.GetAsync("/api/v1/studio/ai/capabilities");
        using var chatResponse = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });

        capabilitiesResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        chatResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertInteractivePrincipalRequiredProblemAsync(capabilitiesResponse);
        await AssertInteractivePrincipalRequiredProblemAsync(chatResponse);
        audit.Recorded.Should().HaveCount(2);
        audit.Recorded.Should().OnlyContain(auditEvent =>
            auditEvent.Action == "studio.lifecycle" &&
            auditEvent.Outcome == AuditOutcome.Denied &&
            auditEvent.Details == "{\"code\":\"studio_authorization/interactive_principal_required\"}");
        audit.Recorded.Should().NotContain(auditEvent => auditEvent.Action == "studio_ai.chat",
            "an unrelated API key must be denied before any model-provider call begins");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public void PostChat_RetainsThirtyRequestsPerMinuteRateLimit()
    {
        var chatEndpoint = _fixture.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                "/api/v{version:apiVersion}/studio/ai/chat",
                StringComparison.Ordinal));

        var rateLimit = chatEndpoint.Metadata.GetMetadata<RateLimitAttribute>();
        rateLimit.Should().NotBeNull();
        rateLimit!.RequestsPerMinute.Should().Be(30);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task PostChat_Anonymous_Returns401()
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task PostChat_UnknownProvider_Returns400AndDoesNotAudit()
    {
        var client = _fixture.CreateAdminClient();
        _audit.Recorded.Clear();

        var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            provider = "does-not-exist",
            messages = new[] { new { role = "user", content = "hi" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _audit.Recorded.Should().NotContain(e => e.Action == "studio_ai.chat",
            because: "a request that never resolves to a configured provider has no action to attribute");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task PostChat_KnownButUnreachableProvider_StreamsErrorEventAndRecordsOneFailureAudit()
    {
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var adminKey = await apiKeyStore.CreateAsync(
            "studio-ai-audit-admin",
            ["admin:write"],
            null,
            null,
            CancellationToken.None);
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", adminKey.Key));
        _audit.Recorded.Clear();

        var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });

        // The request named a real, configured provider, so the SSE response headers are already
        // committed by the time the (unreachable) provider call fails — the failure surfaces as an
        // `error` frame in a 200 SSE body, not as an HTTP error status. The connection failure
        // happens before any bytes come back from the provider, so `message_start` (emitted only
        // once a response is actually received) never fires — just the terminal `error` frame.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: error");

        var chatAudits = _audit.Recorded.Where(e => e.Action == "studio_ai.chat").ToList();
        chatAudits.Should().ContainSingle("exactly one audit record must be written per call, success or failure");
        var audit = chatAudits[0];
        audit.EventType.Should().Be(AuditEventType.AdminAction);
        audit.ResourceType.Should().Be("studio_ai_provider");
        audit.ResourceId.Should().Be(ProviderName);
        audit.Outcome.Should().Be(AuditOutcome.Failure);
        audit.Actor.Should().Be(adminKey.Record.Id.ToString("D"));
        audit.ActorType.Should().Be(AuditActorType.ApiKey);
        audit.Details.Should().Contain("\"kind\":\"openai\"");
    }

    private static void ConfigureHost(IWebHostBuilder builder, bool endUserAuthorizationEnabled)
    {
        // WebAppFixture's common host settings enable a dev-auth bypass
        // (HONUA_DEV_AUTH=true) that auto-authenticates every request as admin in the
        // "Test" environment. Disable it so every role assertion reflects the credential
        // actually sent by the test client.
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.UseSetting(
            "Studio:EndUserAuthorization:Enabled",
            endUserAuthorizationEnabled ? "true" : "false");
        builder.UseSetting("Oidc:Enabled", "true");
        builder.UseSetting("Oidc:RequireHttps", "true");
        builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
        builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
        builder.UseSetting("Oidc:Generic:Enabled", "true");
        builder.UseSetting("Oidc:Generic:Authority", Issuer);
        builder.UseSetting("Oidc:Generic:ClientId", Audience);
        builder.UseSetting("Oidc:Generic:DisplayName", "Studio test IdP");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StudioAiProxy:Enabled"] = "true",
                ["StudioAiProxy:DefaultProvider"] = ProviderName,
                [$"StudioAiProxy:Providers:{ProviderName}:Kind"] = "openai",
                // Port 1 is a privileged port nothing listens on in the test sandbox, so the
                // adapter's HTTP call fails fast (connection refused) instead of hanging.
                [$"StudioAiProxy:Providers:{ProviderName}:Endpoint"] = "http://127.0.0.1:1/v1",
                [$"StudioAiProxy:Providers:{ProviderName}:Model"] = "test-model",
                [$"StudioAiProxy:Providers:{ProviderName}:ApiKey"] = "test-key",
                [$"StudioAiProxy:Providers:{ProviderName}:TimeoutSeconds"] = "5"
            });
        });
    }

    private static async Task<WebAppFixture> CreateEndUserFixtureAsync(CapturingAuditLog audit)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IAuditLog>(audit);
            })
            .ConfigureWebHost(builder => ConfigureHost(builder, endUserAuthorizationEnabled: true));
        await fixture.InitializeAsync();
        return fixture;
    }

    private static HttpClient CreateBearerClient(WebAppFixture fixture, string token)
        => fixture.CreateClient(client =>
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token));

    private static string CreateToken(
        string subject,
        bool isClientCredentials = false,
        string? tenantId = null)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("name", "Studio Test User"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (isClientCredentials)
        {
            // Some external IdPs omit nonstandard grant markers from client-credentials
            // access tokens and may still emit a weak amr=pwd claim. That evidence must not
            // be promoted to an interactive Studio session.
            claims.Add(new Claim("amr", "pwd"));
        }
        else
        {
            claims.Add(new Claim("sid", $"session-{subject}"));
            claims.Add(new Claim(
                "auth_time",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage BuildMcpRequest(string json, string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        return request;
    }

    private static async Task AssertEndUserModeDisabledProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().Should().Be(
            "studio_authorization/end_user_mode_disabled");
    }

    private static async Task AssertInteractivePrincipalRequiredProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().Should().Be(
            "studio_authorization/interactive_principal_required");
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
