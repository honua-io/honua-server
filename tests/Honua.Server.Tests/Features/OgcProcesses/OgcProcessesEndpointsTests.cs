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

namespace Honua.Server.Tests.Features.OgcProcesses;

/// <summary>
/// Integration tests for OGC API Processes endpoints.
/// Tests the adapter layer over the canonical geoprocessing runtime.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiProcesses)]
public sealed class OgcProcessesEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    // -----------------------------------------------------------------------
    // Landing page
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes")]
    public async Task LandingPage_ReturnsValidResponse()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("title").GetString().Should().Contain("Honua");

        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().NotBeEmpty();
        links.Should().Contain(l =>
            l.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/processes");
    }

    // -----------------------------------------------------------------------
    // Conformance
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/processes/conformance")]
    public async Task Conformance_ReturnsProcessesCoreClasses()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/conformance");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var conformsTo = json.RootElement.GetProperty("conformsTo").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/core");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/json");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/dismiss");
        conformsTo.Should().Contain("http://www.opengis.net/spec/ogcapi-processes-1/1.0/conf/job-list");
    }

    // -----------------------------------------------------------------------
    // Process list
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes")]
    public async Task ProcessList_ReturnsAtLeastOneProcess()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var processes = json.RootElement.GetProperty("processes").EnumerateArray().ToArray();
        processes.Should().NotBeEmpty();

        var first = processes[0];
        first.GetProperty("id").GetString().Should().Be("honua-geoprocessing");
        first.TryGetProperty("jobControlOptions", out var jco).Should().BeTrue();
        jco.EnumerateArray().Select(e => e.GetString()).Should().Contain("async-execute");
    }

    // -----------------------------------------------------------------------
    // Process description
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_ValidId_ReturnsDescription()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/honua-geoprocessing");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("id").GetString().Should().Be("honua-geoprocessing");
        json.RootElement.TryGetProperty("inputs", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("outputs", out _).Should().BeTrue();
        json.RootElement.GetProperty("jobControlOptions").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("async-execute");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessDiscovery)]
    [Endpoint("GET /ogc/processes/processes/{processId}")]
    public async Task ProcessDescription_InvalidId_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/processes/nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should()
            .Contain("no-such-process");
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_WithoutRespondAsync_Returns501()
    {
        var body = """{"inputs":{"plan":{"steps":[]}}}""";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/ogc/processes/processes/honua-geoprocessing/execution", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Be("about:blank");
        json.RootElement.GetProperty("type").GetString().Should()
            .NotContain("no-such-process", "sync rejection must not reuse the no-such-process problem type");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_MissingPlanInput_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("title").GetString().Should().Contain("Invalid");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_PlanMissingPlanId_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"steps":[{"stepId":"s1"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("planId");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_PlanEmptySteps_Returns400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"test-plan","steps":[]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("step");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_InvalidProcess_Returns404()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/nonexistent/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent("""{"inputs":{"plan":{"steps":[]}}}""", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // Job list
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_ReturnsJobListObjectOrServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs");

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // No Redis — 503 with problem document
            var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            err.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        json.RootElement.TryGetProperty("jobs", out var jobs).Should().BeTrue();
        jobs.ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.TryGetProperty("links", out _).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Job status
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_NonexistentJob_ReturnsNotFoundOrServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/nonexistent-job-id");

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var err = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            err.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should()
            .Contain("no-such-job");
    }

    // -----------------------------------------------------------------------
    // Job results
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_NonexistentJob_ReturnsNotFoundOrServiceUnavailable()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/nonexistent-job-id/results");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    // -----------------------------------------------------------------------
    // Dismiss
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_NonexistentJob_ReturnsNotFoundOrServiceUnavailable()
    {
        var response = await _fixture.Client.DeleteAsync("/ogc/processes/jobs/nonexistent-job-id");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }
}
