// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.OperateStatus;

/// <summary>
/// Integration tests for the server-authoritative aggregated operate-status endpoint and the
/// read-only ops-reader authorization split (A12). Compile locally; the full assertions run in CI
/// (Testcontainers).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.HealthCheck)]
public sealed class OperateStatusEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "operate-status-admin-key";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public OperateStatusEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                // Configure the availability SLO so the payload evaluates (rather than reporting
                // not-configured) on this fixture.
                builder.UseSetting("Slo:Availability:Target", "0.995");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/operate/status")]
    public async Task GetStatus_WithAdminAuth_ReturnsSelfDescribingVerdictAndDomains()
    {
        var response = await _client.GetAsync("/api/v1/operate/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("schemaVersion").GetString().Should().Be("1.0");
        root.TryGetProperty("generatedAt", out _).Should().BeTrue();
        root.GetProperty("status").GetString().Should().BeOneOf("healthy", "degraded", "unhealthy");
        root.GetProperty("reasons").ValueKind.Should().Be(JsonValueKind.Array);

        var domains = root.GetProperty("domains");
        foreach (var domain in new[] { "deploys", "jobs", "alerts", "migrations", "findings", "telemetryBackends" })
        {
            domains.TryGetProperty(domain, out var view).Should().BeTrue($"domain '{domain}' must be present");
            view.GetProperty("source").GetString().Should().NotBeNullOrWhiteSpace(
                $"domain '{domain}' must carry a drill-down source hint");
        }

        // findings rollup carries a severity breakdown so a copilot need not recount.
        var findings = domains.GetProperty("findings");
        findings.TryGetProperty("total", out _).Should().BeTrue();
        findings.GetProperty("bySeverity").TryGetProperty("critical", out _).Should().BeTrue();

        // SLO configured on this fixture => it evaluates an availability block.
        var slo = root.GetProperty("slo");
        slo.GetProperty("configured").GetBoolean().Should().BeTrue();
        slo.GetProperty("availability").GetProperty("target").GetDouble().Should().Be(0.995);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/operate/status")]
    public async Task GetStatus_WithoutAuth_IsUnauthorized()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/operate/status");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/operate/status")]
    public async Task GetStatus_WithOpsReadKey_IsAuthorized()
    {
        using var opsReader = await CreateOpsReaderClientAsync();

        var response = await opsReader.GetAsync("/api/v1/operate/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/operate/status")]
    [Endpoint("GET /api/v1/admin/observability/findings")]
    public async Task OpsReadKey_CanReadStatusAndFindings()
    {
        using var opsReader = await CreateOpsReaderClientAsync();

        (await opsReader.GetAsync("/api/v1/operate/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await opsReader.GetAsync("/api/v1/admin/observability/findings")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/deploy/operations/{operationId}/rollback")]
    [Endpoint("POST /api/v1/admin/observability/findings/{findingId}/propose")]
    public async Task OpsReadKey_CannotInvokeMutatingOpsEndpoints()
    {
        using var opsReader = await CreateOpsReaderClientAsync();

        // Mutating deploy endpoint keeps the admin policy: an ops-reader (non-admin scoped) key is 403.
        var rollback = await opsReader.PostAsync(
            "/api/v1/admin/deploy/operations/does-not-exist/rollback", content: null);
        rollback.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Mutating propose endpoint shares the method-aware ops-read group but still requires admin write.
        var propose = await opsReader.PostAsync(
            "/api/v1/admin/observability/findings/does-not-exist/propose", content: null);
        propose.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/operate/status")]
    public async Task OpsReadKey_IsNarrowerThanAdminRead_DeniedNonOpsAdminSurface()
    {
        using var opsReader = await CreateOpsReaderClientAsync();

        // ops:read is scoped to the ops-observability surfaces; it does NOT grant admin key-management.
        var adminSurface = await opsReader.GetAsync("/api/v1/admin/api-keys");
        adminSurface.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> CreateOpsReaderClientAsync()
    {
        var request = new CreateAdminApiKeyRequest
        {
            Name = $"ops-reader-{Guid.NewGuid():N}",
            Permissions = ["ops:read"],
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/api-keys", request, _jsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = JsonSerializer.Deserialize<ApiResponse<AdminApiKeySecretResponse>>(
            await response.Content.ReadAsStringAsync(), _jsonOptions);
        created!.Data.Should().NotBeNull();

        return _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", created.Data.Key));
    }
}
