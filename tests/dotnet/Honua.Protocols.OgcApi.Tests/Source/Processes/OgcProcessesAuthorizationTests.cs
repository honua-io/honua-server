// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// Authorization tests for OGC API Processes protected endpoints.
/// Verifies that protected routes return 401 when dev-auth is disabled.
/// </summary>
[Collection("Database.OgcApiData")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesAuthorizationTests : IClassFixture<OgcProcessesAuthorizationTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public OgcProcessesAuthorizationTests(OgcProcessesAuthorizationTestsFixture fixture) => _fixture = fixture.App;

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_WithoutAuth_ReturnsUnauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(
            """{"inputs":{"plan":{"planId":"p1","steps":[{"stepId":"s1","kind":"geoprocess"}]}}}""",
            Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_WithMalformedJsonWithoutAuth_ReturnsUnauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/ogc/processes/processes/honua-geoprocessing/execution");
        request.Content = new StringContent("{", Encoding.UTF8, "application/json");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs")]
    public async Task JobList_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.JobStatus)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    public async Task JobStatus_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/any-job-id");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.JobResults)]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task JobResults_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.GetAsync("/ogc/processes/jobs/any-job-id/results");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task DismissJob_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _fixture.Client.DeleteAsync("/ogc/processes/jobs/any-job-id");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Per-class wrapper that owns a single <see cref="WebAppFixture"/> configured
/// with dev-auth disabled, shared across all tests in
/// <see cref="OgcProcessesAuthorizationTests"/> via <see cref="IClassFixture{T}"/>.
/// </summary>
public sealed class OgcProcessesAuthorizationTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
        });

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}
