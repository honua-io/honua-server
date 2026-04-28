// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class PMTilesPublishEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task StartJob_PublishOperation_ReturnsAccepted()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "publish",
            layerId = WebAppFixture.TestLayerId,
            minZoom = 0,
            maxZoom = 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task StartJob_PublishWithoutLayerId_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            "/api/v1/admin/tile-operations/jobs",
            new StringContent("""{"operation":"publish"}""", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs/{jobId}")]
    public async Task GetJobStatus_PublishJob_ReturnsPublishedArtifactDescriptor()
    {
        var jobId = await StartPublishJobAsync();
        var (status, finalJson) = await WaitForJobCompletionAsync(jobId);

        status.Should().NotBeNull("publish job should have completed within timeout");

        var root = finalJson!.RootElement;
        GetPropertyCaseInsensitive(root, "operation").GetString().Should().Be("publish");

        if (status == OperationStatus.Completed)
        {
            var descriptor = GetPropertyCaseInsensitive(root, "publishedArtifact");
            descriptor.ValueKind.Should().Be(JsonValueKind.Object);
            GetPropertyCaseInsensitive(descriptor, "artifactId").GetString().Should().NotBeNullOrWhiteSpace();
            GetPropertyCaseInsensitive(descriptor, "objectKey").GetString().Should().NotBeNullOrWhiteSpace();
            GetPropertyCaseInsensitive(descriptor, "contentType").GetString().Should().Be("application/vnd.pmtiles");
            GetPropertyCaseInsensitive(descriptor, "sizeBytes").GetInt64().Should().BeGreaterThan(0);
            GetPropertyCaseInsensitive(descriptor, "urlStrategy").GetString().Should().NotBeNullOrWhiteSpace();
            GetPropertyCaseInsensitive(descriptor, "accessUrl").GetString().Should().NotBeNullOrWhiteSpace();
            GetPropertyCaseInsensitive(descriptor, "minZoom").GetInt32().Should().BeGreaterOrEqualTo(0);
            GetPropertyCaseInsensitive(descriptor, "maxZoom").GetInt32().Should().BeGreaterOrEqualTo(0);
            GetPropertyCaseInsensitive(descriptor, "bounds").ValueKind.Should().Be(JsonValueKind.Array);
        }

        finalJson.Dispose();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{artifactId}")]
    public async Task PMTilesProxy_RangeRequest_Returns206WithRangeHeaders()
    {
        var jobId = await StartPublishJobAsync();
        var (status, finalJson) = await WaitForJobCompletionAsync(jobId);
        status.Should().Be(OperationStatus.Completed, "publish job must complete to test the proxy");

        var artifactId = GetPropertyCaseInsensitive(
            GetPropertyCaseInsensitive(finalJson!.RootElement, "publishedArtifact"),
            "artifactId").GetString()!;
        finalJson.Dispose();

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tiles/pmtiles/{artifactId}");
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 6);

        using var rangeResponse = await _client.SendAsync(rangeRequest);

        rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        rangeResponse.Headers.AcceptRanges.Should().Contain("bytes");
        rangeResponse.Content.Headers.ContentRange.Should().NotBeNull();
        rangeResponse.Content.Headers.ContentRange!.Unit.Should().Be("bytes");

        var bytes = await rangeResponse.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().Be(7);
        bytes[..7].Should().BeEquivalentTo("PMTiles"u8.ToArray(), "PMTiles archive header should appear at offset 0");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{artifactId}")]
    public async Task PMTilesProxy_MissingArtifact_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/tiles/pmtiles/{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{artifactId}")]
    public async Task PMTilesProxy_UnsatisfiableRange_Returns416()
    {
        var jobId = await StartPublishJobAsync();
        var (status, finalJson) = await WaitForJobCompletionAsync(jobId);
        status.Should().Be(OperationStatus.Completed);
        var artifactId = GetPropertyCaseInsensitive(
            GetPropertyCaseInsensitive(finalJson!.RootElement, "publishedArtifact"),
            "artifactId").GetString()!;
        finalJson.Dispose();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tiles/pmtiles/{artifactId}");
        request.Headers.TryAddWithoutValidation("Range", "bytes=99999999999-100000000000");

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    private async Task<string> StartPublishJobAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "publish",
            layerId = WebAppFixture.TestLayerId,
            minZoom = 0,
            maxZoom = 1,
            maxTiles = 5
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString()!;
    }

    private async Task<(OperationStatus? Status, JsonDocument? Json)> WaitForJobCompletionAsync(string jobId, int timeoutSeconds = 30)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JsonDocument? lastJson = null;

        while (DateTime.UtcNow < timeout)
        {
            var response = await _client.GetAsync($"/api/v1/admin/tile-operations/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            lastJson?.Dispose();
            lastJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = (OperationStatus)GetPropertyCaseInsensitive(lastJson.RootElement, "status").GetInt32();
            if (status is OperationStatus.Completed or OperationStatus.Failed)
            {
                return (status, lastJson);
            }

            await Task.Delay(200);
        }

        return (null, lastJson);
    }

    private static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}
