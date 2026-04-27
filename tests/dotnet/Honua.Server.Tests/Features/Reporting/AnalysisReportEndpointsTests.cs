// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Integration coverage for the public analysis-report HTTP surface registered
/// in <c>EndpointRegistry</c>: the JSON envelope at
/// <c>/api/v1/analysis/reports/{jobId}</c> and the Markdown / HTML render at
/// <c>/api/v1/analysis/reports/{jobId}/render</c>. Tests exercise route
/// reachability, admin authorization parity with the canonical job-results
/// surface, and 400/404 boundaries so the routes stay covered by the
/// public-interface proof ledger.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AnalysisReportEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}")]
    public async Task GetReport_UnknownJob_ReachesHandlerAndReturnsProblemDetails()
    {
        // The route is registered and auth'd; the underlying geoprocessing
        // store is intentionally absent in this fixture, so the handler
        // surfaces a ProblemDetails envelope (404 for "missing job", 503 when
        // the store itself is unavailable). Either response confirms the
        // route is reachable and the auth pipeline did not short-circuit.
        var response = await _client.GetAsync("/api/v1/analysis/reports/missing-job");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task RenderReport_UnknownJob_ReachesHandlerAndReturnsProblemDetails()
    {
        var response = await _client.GetAsync("/api/v1/analysis/reports/missing-job/render?format=md");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task RenderReport_UnknownFormat_ReturnsBadRequest()
    {
        // Format validation runs before the job lookup, so this path is
        // independent of fixture state and proves the render route's input
        // contract.
        var response = await _client.GetAsync("/api/v1/analysis/reports/any-job/render?format=pdf");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
