// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Regression coverage for the arcgis-stub Client Compatibility Smoke gate
// (docker/client-compat/arcgis-stub/stub_runner.py) CERT-ERRH-01 / CERT-ERRH-02
// error-handling certification checks. The stub grades these by asserting the
// HTTP status is in (400, 404) OR the response body contains "error" (ERRH-01),
// and status in (200, 400) AND the body contains "error"/"Invalid" (ERRH-02).
//
// Honua's GeoServices convention returns errors as HTTP 200 with an Esri error
// envelope ({"error":{"code":N,...}}), which satisfies both. These tests lock in
// that an unknown layer id (MapServer + FeatureServer) and a malformed `where`
// filter surface an error envelope rather than a non-error 200.

using System;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Verifies the GeoServices error-handling certification checks (CERT-ERRH-01 /
/// CERT-ERRH-02) exercised by the in-repo arcgis-stub Client Compatibility gate.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class ClientCompatErrorHandlingTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const int UnknownLayerId = 99999;

    public ClientCompatErrorHandlingTests(WebAppFixture fixture) => _fixture = fixture;

    // Mirrors stub_runner.py CERT-ERRH-01 grading: pass if status in (400,404) OR
    // the body contains "error" (case-insensitive).
    private static void AssertErrht01(HttpResponseMessage response, string body)
    {
        var statusOk = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;
        (statusOk || body.Contains("error", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue($"CERT-ERRH-01 requires an error status or an error body; got {(int)response.StatusCode}: {body}");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServer_UnknownLayerId_ReturnsErrorEnvelope()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/MapServer/{UnknownLayerId}?f=json");
        var body = await response.Content.ReadAsStringAsync();

        AssertErrht01(response, body);

        // Honua's GeoServices convention: HTTP 200 carrying {"error":{"code":404,...}}.
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue(body);
        error.GetProperty("code").GetInt32().Should().Be(404);
        // A nonexistent layer must never be served as a valid layer descriptor.
        doc.RootElement.TryGetProperty("fields", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServer_UnknownLayerId_ReturnsErrorEnvelope()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{UnknownLayerId}?f=json");
        var body = await response.Content.ReadAsStringAsync();

        AssertErrht01(response, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue(body);
        error.GetProperty("code").GetInt32().Should().Be(404);
        doc.RootElement.TryGetProperty("fields", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_MalformedWhereClause_ReturnsErrorEnvelope()
    {
        // The exact malformed clause the stub (CERT-ERRH-02) issues.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?where=this%20is%20not%20a%20where%20clause&f=json");
        var body = await response.Content.ReadAsStringAsync();

        // Mirrors stub_runner.py CERT-ERRH-02 grading.
        var statusOk = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest;
        var bodyOk = body.Contains("error", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Invalid", StringComparison.Ordinal);
        (statusOk && bodyOk).Should().BeTrue(
            $"CERT-ERRH-02 requires a 200/400 with an error/Invalid body; got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue(
            $"a malformed where clause must surface an Esri error envelope, not rows/zero-results: {body}");
    }
}
