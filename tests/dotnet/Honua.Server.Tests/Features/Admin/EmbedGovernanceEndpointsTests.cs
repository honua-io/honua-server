// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Admin.EmbedGovernance.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for embed governance: key lifecycle, scoped policy
/// evaluation, CORS/domain enforcement, rate limiting, analytics ingestion, and
/// usage aggregation.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApiKeyManagement)]
public sealed class EmbedGovernanceEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "embed-governance-admin-bootstrap-key";
    private const string EmbedKeyHeader = "X-Honua-Embed-Key";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public EmbedGovernanceEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/embed/keys")]
    [Endpoint("GET /api/v1/admin/embed/keys")]
    [Endpoint("GET /api/v1/admin/embed/keys/{id}")]
    public async Task CreateListAndGetEmbedKey_ReturnsScopeAndSecretOnce()
    {
        var created = await CreateKeyAsync("contract-key", ["https://app.example.com"]);

        Assert.StartsWith("embk_", created.Key, StringComparison.Ordinal);
        Assert.Equal("contract-key", created.EmbedKey.Name);
        Assert.Equal("active", created.EmbedKey.Status);
        Assert.Contains("https://app.example.com", created.EmbedKey.Scope.AllowedEmbedOrigins);

        var listResponse = await _client.GetAsync("/api/v1/admin/embed/keys");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Key, listJson, StringComparison.Ordinal);

        var getResponse = await _client.GetAsync($"/api/v1/admin/embed/keys/{created.EmbedKey.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = JsonSerializer.Deserialize<ApiResponse<EmbedKeyResponse>>(
            await getResponse.Content.ReadAsStringAsync(), _jsonOptions);
        Assert.Equal(created.EmbedKey.Id, fetched?.Data?.Id);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/embed/keys")]
    public async Task CreateEmbedKey_WithoutAllowedOrigins_ReturnsBadRequest()
    {
        var request = new CreateEmbedKeyRequest
        {
            Name = "no-origins",
            Scope = new EmbedKeyScopeDto { AllowedEmbedOrigins = [] },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/embed/keys", request, _jsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("origin", body, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/embed/keys/{id}/rotate")]
    public async Task RotateEmbedKey_InvalidatesOldSecret()
    {
        var created = await CreateKeyAsync("rotate-key", ["https://app.example.com"]);

        var response = await _client.PostAsync($"/api/v1/admin/embed/keys/{created.EmbedKey.Id}/rotate", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = JsonSerializer.Deserialize<ApiResponse<EmbedKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(), _jsonOptions);

        Assert.NotNull(rotated);
        Assert.NotNull(rotated.Data);
        Assert.NotEqual(created.Key, rotated.Data.Key);

        var oldKeyPolicy = await GetPolicyAsync(created.Key, "https://app.example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyPolicy.StatusCode);

        var newKeyPolicy = await GetPolicyAsync(rotated.Data.Key, "https://app.example.com");
        Assert.Equal(HttpStatusCode.OK, newKeyPolicy.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/embed/keys/{id}/revoke")]
    public async Task RevokeEmbedKey_BlocksPolicyIssuance()
    {
        var created = await CreateKeyAsync("revoke-key", ["https://app.example.com"]);

        var revoke = await _client.PostAsync($"/api/v1/admin/embed/keys/{created.EmbedKey.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var policy = await GetPolicyAsync(created.Key, "https://app.example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, policy.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/embed/policy")]
    public async Task FetchPolicy_AllowedOrigin_ReturnsPolicyWithCorsHeader()
    {
        var created = await CreateKeyAsync("policy-key", ["https://app.example.com"], integrationId: "site-7");

        var response = await GetPolicyAsync(created.Key, "https://app.example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://app.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var policy = JsonSerializer.Deserialize<EmbedPolicyResponse>(
            await response.Content.ReadAsStringAsync(), _jsonOptions);
        Assert.NotNull(policy);
        Assert.Equal("site-7", policy!.IntegrationId);
        Assert.Contains("https://app.example.com", policy.AllowedOrigins);
        Assert.NotEmpty(policy.Capabilities);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/embed/policy")]
    public async Task FetchPolicy_DisallowedOrigin_ReturnsForbidden()
    {
        var created = await CreateKeyAsync("policy-deny-key", ["https://app.example.com"]);

        var response = await GetPolicyAsync(created.Key, "https://evil.example.org");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OriginNotAllowed", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/embed/policy")]
    public async Task FetchPolicy_MissingKey_ReturnsUnauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/embed/policy");
        request.Headers.Add("Origin", "https://app.example.com");

        using var anonymous = _fixture.CreateClient();
        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/embed/analytics")]
    [Endpoint("GET /api/v1/admin/embed/usage")]
    public async Task IngestAnalyticsThenQueryUsage_AggregatesEvents()
    {
        var created = await CreateKeyAsync("analytics-key", ["https://app.example.com"], integrationId: "site-analytics");

        var payload = new IngestEmbedAnalyticsRequest
        {
            Events =
            [
                new EmbedAnalyticsEventDto { EventType = "view", Origin = "https://app.example.com", ServiceId = "services/Roads" },
                new EmbedAnalyticsEventDto { EventType = "search", Origin = "https://app.example.com", ServiceId = "services/Roads" },
            ],
        };

        var ingest = await PostAnalyticsAsync(created.Key, payload);
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        var ingestResult = JsonSerializer.Deserialize<ApiResponse<EmbedAnalyticsIngestResponse>>(
            await ingest.Content.ReadAsStringAsync(), _jsonOptions);
        Assert.Equal(2, ingestResult?.Data?.Accepted);

        var usage = await _client.GetAsync("/api/v1/admin/embed/usage?groupBy=eventType&integrationId=site-analytics");
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        var report = JsonSerializer.Deserialize<ApiResponse<EmbedUsageResponse>>(
            await usage.Content.ReadAsStringAsync(), _jsonOptions);
        Assert.NotNull(report);
        Assert.NotNull(report.Data);
        Assert.True(report.Data.Total >= 2);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/embed/policy")]
    public async Task FetchPolicy_CrossOriginPreflightFromArbitraryOrigin_IsAllowed()
    {
        var created = await CreateKeyAsync("policy-cors-key", ["https://isv.example.com"], integrationId: "site-cors");

        using var anonymous = _fixture.CreateClient();

        // A browser CORS preflight from an arbitrary ISV origin must be permitted; the
        // per-embed-key origin allow-list is enforced by the handler, not by CORS.
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/embed/policy");
        preflight.Headers.Add("Origin", "https://isv.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-honua-embed-key");
        var preflightResponse = await anonymous.SendAsync(preflight);

        Assert.True(
            preflightResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"preflight returned {(int)preflightResponse.StatusCode}");
        Assert.True(
            preflightResponse.Headers.Contains("Access-Control-Allow-Origin"),
            "preflight response missing Access-Control-Allow-Origin");

        var allowedHeaders = preflightResponse.Headers.TryGetValues("Access-Control-Allow-Headers", out var values)
            ? string.Join(",", values)
            : string.Empty;
        Assert.Contains("x-honua-embed-key", allowedHeaders, StringComparison.OrdinalIgnoreCase);

        // The subsequent cross-origin GET succeeds and echoes the approved origin.
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/embed/policy");
        get.Headers.Add(EmbedKeyHeader, created.Key);
        get.Headers.Add("Origin", "https://isv.example.com");
        var getResponse = await anonymous.SendAsync(get);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(
            "https://isv.example.com",
            getResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/embed/analytics")]
    [Endpoint("GET /api/v1/admin/embed/usage")]
    public async Task IngestAnalytics_BatchWithOneInvalidEvent_PersistsZeroEvents()
    {
        var created = await CreateKeyAsync("analytics-atomic-key", ["https://app.example.com"], integrationId: "site-atomic");

        var payload = new IngestEmbedAnalyticsRequest
        {
            Events =
            [
                // Valid event first: under the old per-event persistence this would commit
                // before the second event rejects, double-counting on client retry.
                new EmbedAnalyticsEventDto { EventType = "view", Origin = "https://app.example.com", ServiceId = "services/Roads" },
                // Invalid: a raw embed key in the payload is rejected by the validator, so the
                // entire batch must be rejected and nothing persisted.
                new EmbedAnalyticsEventDto { EventType = "search", IntegrationId = created.Key },
            ],
        };

        var response = await PostAnalyticsAsync(created.Key, payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var usage = await _client.GetAsync("/api/v1/admin/embed/usage?groupBy=eventType&integrationId=site-atomic");
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        var report = JsonSerializer.Deserialize<ApiResponse<EmbedUsageResponse>>(
            await usage.Content.ReadAsStringAsync(), _jsonOptions);
        Assert.NotNull(report);
        Assert.NotNull(report.Data);
        Assert.Equal(0L, report.Data.Total);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/embed/analytics")]
    public async Task IngestAnalytics_WithRawKeyInPayload_ReturnsBadRequest()
    {
        var created = await CreateKeyAsync("analytics-redaction-key", ["https://app.example.com"]);

        var payload = new IngestEmbedAnalyticsRequest
        {
            Events =
            [
                new EmbedAnalyticsEventDto { EventType = "view", IntegrationId = created.Key },
            ],
        };

        var response = await PostAnalyticsAsync(created.Key, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("raw embed key", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EmbedKeySecretResponse> CreateKeyAsync(
        string name,
        IReadOnlyList<string> origins,
        string? integrationId = null,
        int rateLimit = 0)
    {
        var request = new CreateEmbedKeyRequest
        {
            Name = name,
            Scope = new EmbedKeyScopeDto
            {
                AllowedEmbedOrigins = origins,
                IntegrationId = integrationId,
                RateLimitRequestsPerWindow = rateLimit,
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/embed/keys", request, _jsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = JsonSerializer.Deserialize<ApiResponse<EmbedKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(), _jsonOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        return result.Data;
    }

    private async Task<HttpResponseMessage> GetPolicyAsync(string embedKey, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/embed/policy");
        request.Headers.Add(EmbedKeyHeader, embedKey);
        request.Headers.Add("Origin", origin);
        using var anonymous = _fixture.CreateClient();
        return await anonymous.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostAnalyticsAsync(string embedKey, IngestEmbedAnalyticsRequest payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/embed/analytics")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(EmbedKeyHeader, embedKey);
        using var anonymous = _fixture.CreateClient();
        return await anonymous.SendAsync(request);
    }
}
