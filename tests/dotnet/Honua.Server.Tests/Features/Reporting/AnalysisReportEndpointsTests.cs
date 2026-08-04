// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

/// <summary>
/// Happy-path coverage for honua-server#2945: prior proving tests for this surface were
/// error-paths only (the store is intentionally absent above so a real job never resolves).
/// This registers a fake <see cref="IGeoprocessingJobService"/> that returns a completed
/// <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisResultPackage"/> (reusing
/// <see cref="ReportingFixtures.BufferAggregatePackage"/>, the same fixture the real
/// <c>AnalysisReportService</c>/<c>AnalysisReportBuilder</c> unit tests assert against) so the
/// REAL report builder, store, and Markdown/HTML renderers run end to end through the actual
/// HTTP surface, proving a report genuinely generates and renders rather than only erroring.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AnalysisReportEndpointsHappyPathTests : IAsyncLifetime
{
    private const string JobId = "reporting-happy-path-job";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public AnalysisReportEndpointsHappyPathTests()
    {
        _fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton<IGeoprocessingJobService>(new CompletedJobService());
        });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}")]
    public async Task GetReport_CompletedJob_GeneratesRealReportEnvelope()
    {
        var response = await _client.GetAsync($"/api/v1/analysis/reports/{JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"jobId\":\"" + JobId + "\"");
        content.Should().Contain("Buffered places");
        content.Should().Contain("500m buffers applied to the seed places layer.");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task RenderReport_CompletedJob_RendersRealMarkdown()
    {
        var response = await _client.GetAsync($"/api/v1/analysis/reports/{JobId}/render?format=md");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/markdown");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("# Buffered places");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/analysis/reports/{jobId}/render")]
    public async Task RenderReport_CompletedJob_RendersRealHtml()
    {
        var response = await _client.GetAsync($"/api/v1/analysis/reports/{JobId}/render?format=html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Buffered places");
    }

    /// <summary>
    /// Minimal <see cref="IGeoprocessingJobService"/> test double that only implements the
    /// two members <c>AnalysisReportService</c> actually calls (authorization pass-through +
    /// result-package lookup); every other member throws <see cref="NotSupportedException"/>
    /// since the reporting surface never calls them (mirrors the pattern already used by
    /// <c>OgcProcessesJobResultsTestsFixture.ArtifactBackedJobService</c>).
    /// </summary>
    private sealed class CompletedJobService : IGeoprocessingJobService
    {
        public Task EnsureCallerAuthorizedAsync(
            ClaimsPrincipal principal,
            OperatorResourceType resourceType,
            OperatorOperation operation,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AnalysisPlan> EnsurePlanExecutionTierAuthorizedAsync(
            AnalysisPlan plan,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public PlanValidationResult ValidatePlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public DryRunResult DryRunPlan(AnalysisPlan plan, ClaimsPrincipal principal)
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> SubmitJobAsync(
            AnalysisPlan plan,
            string? idempotencyKey,
            ClaimsPrincipal principal,
            IReadOnlyDictionary<string, string>? protocolMetadata = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExecutionJobRecord> GetJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeoprocessingJobListPage> ListJobsAsync(
            GeoprocessingJobListFilter filter,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisResultPackage> GetJobResultsAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ReportingFixtures.BufferAggregatePackage() with { ResultPackageId = $"{jobId}:v1" });

        public Task CancelJobAsync(
            string jobId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
