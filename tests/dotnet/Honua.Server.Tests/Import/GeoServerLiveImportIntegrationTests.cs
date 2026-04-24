// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests that exercise GeoServer import endpoints against a live GeoServer container.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoServerLiveImportIntegrationTests : IAsyncLifetime
{
    private readonly GeoServerFixture _geoServer = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public GeoServerLiveImportIntegrationTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [GeoServerImportExecutionSettings.AllowUnsafeLocalUrlsInTestEnvironmentKey] = bool.TrueString
                });
            }));
    }

    public async Task InitializeAsync()
    {
        await _geoServer.InitializeAsync();
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _geoServer.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/discover")]
    public async Task Discover_LiveGeoServerHarness_ReturnsDiscoveredResources()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/discover", new
        {
            GeoServerRestUrl = _geoServer.RestUrl,
            Username = _geoServer.Username,
            Password = _geoServer.Password,
            IncludeCompatibilityAnalysis = true,
            IncludeStyleContent = false,
            TimeoutSeconds = 120
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
        payload.Should().NotBeNull();
        payload!.RootElement.GetProperty("geoServerRestUrl").GetString().Should().Be(_geoServer.RestUrl);
        payload.RootElement.GetProperty("workspaces").GetArrayLength().Should().BeGreaterThan(0);
        payload.RootElement.GetProperty("layers").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/geoserver/start")]
    public async Task Start_LiveGeoServerHarnessDryRun_Completes()
    {
        const string envKey = "HONUA_TEST_GEOSERVER_LIVE_PASSWORD";
        var previousValue = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, _geoServer.Password);

        try
        {
            var response = await _client.PostAsJsonAsync("/api/v1/admin/import/geoserver/start", new
            {
                GeoServerRestUrl = _geoServer.RestUrl,
                Username = _geoServer.Username,
                PasswordSecretReference = $"env:{envKey}",
                DryRun = true,
                ImportStyles = false,
                RequestTimeoutSeconds = 120
            });

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var jobId = await GetJobIdAsync(response);
            var completed = await WaitForJobStatusAsync(jobId, "Completed", TimeSpan.FromMinutes(3));

            completed.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
            completed.RootElement.GetProperty("status").GetString().Should().Be("Completed");
            completed.RootElement.GetProperty("geoServerUrl").GetString().Should().Be(_geoServer.RestUrl);
            completed.RootElement.GetProperty("progress").GetProperty("currentPhase").GetString().Should().Be("Dry run completed");
            completed.RootElement.GetProperty("progress").GetProperty("sourceGeoServerUrl").GetString().Should().Be(_geoServer.RestUrl);

            if (completed.RootElement.GetProperty("progress").TryGetProperty("estimatedTotalResources", out var estimatedTotalResources))
            {
                estimatedTotalResources.GetInt32().Should().BeGreaterThan(0);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previousValue);
        }
    }

    private async Task<string> GetJobIdAsync(HttpResponseMessage response)
    {
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return document!.RootElement.GetProperty("jobId").GetString()!;
    }

    private async Task<JsonDocument> WaitForJobStatusAsync(string jobId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/v1/admin/import/geoserver/jobs/{jobId}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var payload = await response.Content.ReadFromJsonAsync<JsonDocument>();
            var status = payload!.RootElement.GetProperty("status").GetString();

            if (status == expectedStatus)
            {
                return payload;
            }

            if (status is "Failed" or "Cancelled")
            {
                var errorMessage = payload.RootElement.TryGetProperty("errorMessage", out var errorElement)
                    ? errorElement.GetString()
                    : "GeoServer import job did not complete successfully.";
                throw new InvalidOperationException($"GeoServer import job {jobId} ended with status {status}: {errorMessage}");
            }

            payload.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException($"Timed out waiting for GeoServer import job '{jobId}' to reach status '{expectedStatus}'.");
    }
}
