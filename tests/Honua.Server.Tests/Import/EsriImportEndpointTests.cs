// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for Esri service import endpoints
/// </summary>
[Collection("Database")]
public class EsriImportEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region POST /api/import/esri/discover

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/discover")]
    public async Task Discover_WithMissingServiceUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/discover", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/discover")]
    public async Task Discover_WithInvalidUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new { ServiceUrl = "not-a-valid-url" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/discover", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("valid HTTP");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/discover")]
    public async Task Discover_WithNonExistentServer_ReturnsBadGateway()
    {
        // Arrange - use a URL that will fail to connect
        var request = new
        {
            ServiceUrl = "https://nonexistent.arcgis.server.invalid/arcgis/rest/services/Test/FeatureServer",
            TimeoutSeconds = 5
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/discover", request);

        // Assert - should return 502 Bad Gateway when can't connect
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    #endregion

    #region POST /api/import/esri/start

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/start")]
    public async Task Start_WithMissingServiceUrl_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            LayerId = 0,
            TableName = "test_table"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/start")]
    public async Task Start_WithMissingTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("TableName is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/start")]
    public async Task Start_WithInvalidTableName_ReturnsBadRequest()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "invalid-table-name!" // Contains invalid characters
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/start", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid table name");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/start")]
    public async Task Start_WithValidRequest_ReturnsAccepted()
    {
        // Arrange
        var request = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_esri_import"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/import/esri/start", request);

        // Assert - should return 202 Accepted with job info
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobId");
        content.Should().Contain("statusUrl");
        content.Should().Contain("cancelUrl");
    }

    #endregion

    #region GET /api/import/esri/jobs/{jobId}

    [IntegrationTest]
    [Endpoint("GET /api/import/esri/jobs/{jobId}")]
    public async Task GetJobStatus_WithNonExistentJob_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/import/esri/jobs/nonexistent123");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/import/esri/jobs/{jobId}")]
    public async Task GetJobStatus_AfterStartingJob_ReturnsProgress()
    {
        // Arrange - start a job first
        var startRequest = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_job_status"
        };

        var startResponse = await _client.PostAsJsonAsync("/api/import/esri/start", startRequest);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startContent = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startContent.GetProperty("jobId").GetString();

        // Act
        var response = await _client.GetAsync($"/api/import/esri/jobs/{jobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobId");
        content.Should().Contain("status");
        content.Should().Contain("tableName");
    }

    #endregion

    #region POST /api/import/esri/jobs/{jobId}/cancel

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithNonExistentJob_ReturnsNotFound()
    {
        // Act
        var response = await _client.PostAsync("/api/import/esri/jobs/nonexistent123/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/esri/jobs/{jobId}/cancel")]
    public async Task CancelJob_AfterStartingJob_ReturnsSuccess()
    {
        // Arrange - start a job first
        var startRequest = new
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer",
            LayerId = 0,
            TableName = "test_cancel_job"
        };

        var startResponse = await _client.PostAsJsonAsync("/api/import/esri/start", startRequest);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startContent = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startContent.GetProperty("jobId").GetString();

        // Act
        var response = await _client.PostAsync($"/api/import/esri/jobs/{jobId}/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(jobId!);
        content.Should().Contain("cancellation");
    }

    #endregion

    #region GET /api/import/esri/jobs

    [IntegrationTest]
    [Endpoint("GET /api/import/esri/jobs")]
    public async Task ListJobs_ReturnsJobsList()
    {
        // Act
        var response = await _client.GetAsync("/api/import/esri/jobs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("jobs");
    }

    #endregion
}
