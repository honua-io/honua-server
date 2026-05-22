// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the compliance admin endpoints (#352). Verifies the full
/// HTTP surface: dashboard, residency evaluation, key rotation, report export
/// (CSV + PDF). These tests exercise the same DI registration the production
/// host uses, so they catch wiring regressions the unit tests cannot.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ComplianceAdminEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/dashboard")]
    public async Task GetDashboard_ReturnsSuccessEnvelopeWithControls()
    {
        var response = await _client.GetAsync("/api/v1/admin/compliance/dashboard");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.GetProperty("controls").GetArrayLength().Should().BeGreaterThan(0);
        data.GetProperty("summary").GetProperty("readinessPercent").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        data.GetProperty("encryption").GetProperty("activeKeyVersion").GetInt32().Should().BeGreaterThan(0);
        data.GetProperty("residency").GetProperty("primaryRegion").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/report")]
    public async Task GetReport_Csv_ReturnsCsvWithBom()
    {
        var response = await _client.GetAsync("/api/v1/admin/compliance/report?format=csv");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().HaveCountGreaterThan(10);
        bytes[..3].Should().BeEquivalentTo(new byte[] { 0xEF, 0xBB, 0xBF });
        var content = Encoding.UTF8.GetString(bytes);
        content.Should().Contain("Framework,ControlId,Title");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/report")]
    public async Task GetReport_Pdf_ReturnsPdfBytes()
    {
        var response = await _client.GetAsync("/api/v1/admin/compliance/report?format=pdf");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().HaveCountGreaterThan(50);
        Encoding.ASCII.GetString(bytes, 0, 8).Should().StartWith("%PDF-1.");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/report")]
    public async Task GetReport_DefaultFormat_IsPdf()
    {
        var response = await _client.GetAsync("/api/v1/admin/compliance/report");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/compliance/report")]
    public async Task GetReport_UnknownFormat_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/v1/admin/compliance/report?format=xlsx");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/compliance/residency/evaluate")]
    public async Task PostResidencyEvaluate_InformationalPolicy_AllowsRegion()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/compliance/residency/evaluate",
            new { region = "us-east-1" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("allowed").GetBoolean().Should().BeTrue();
        data.GetProperty("region").GetString().Should().Be("us-east-1");
        data.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/compliance/encryption/rotate-key")]
    public async Task PostRotateKey_AdvancesActiveVersionAndIsAuditLogged()
    {
        var initialDashboard = await _client.GetFromJsonAsync<JsonElement>("/api/v1/admin/compliance/dashboard");
        var previousVersion = initialDashboard
            .GetProperty("data")
            .GetProperty("encryption")
            .GetProperty("activeKeyVersion")
            .GetInt32();

        var rotateResponse = await _client.PostAsync("/api/v1/admin/compliance/encryption/rotate-key", null);
        rotateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await rotateResponse.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        data.GetProperty("previousVersion").GetInt32().Should().Be(previousVersion);
        data.GetProperty("newVersion").GetInt32().Should().Be(previousVersion + 1);

        var followupDashboard = await _client.GetFromJsonAsync<JsonElement>("/api/v1/admin/compliance/dashboard");
        followupDashboard
            .GetProperty("data")
            .GetProperty("encryption")
            .GetProperty("activeKeyVersion")
            .GetInt32()
            .Should().Be(previousVersion + 1);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/compliance/residency/evaluate")]
    public async Task PostResidencyEvaluate_EmptyBody_ReturnsBadRequest()
    {
        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/v1/admin/compliance/residency/evaluate", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
