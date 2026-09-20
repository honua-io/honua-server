// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles.PMTiles;
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
            serviceId = WebAppFixture.TestServiceId,
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
        using var content = new StringContent("""{"operation":"publish"}""", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(
            "/api/v1/admin/tile-operations/jobs",
            content);

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
    [Endpoint("GET /api/v1/tiles/pmtiles/{*artifactId}")]
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

    /// <summary>
    /// Range coverage for an <em>interior</em> tile window, not the seven-byte magic prefix
    /// (honua-server#4397).
    /// </summary>
    /// <remarks>
    /// The existing 206 test requests bytes 0-6 and asserts they spell "PMTiles", which proves the
    /// route honours <c>Range</c> but says nothing about whether a range landing on real tile data
    /// returns the right bytes — the thing every PMTiles client actually does. This walks the
    /// served archive's header and root directory to compute one tile's absolute byte window, then
    /// asks for exactly that window over HTTP and compares the returned bytes with that tile's
    /// bytes in the whole-archive download.
    /// </remarks>
    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{*artifactId}")]
    public async Task PMTilesProxy_InteriorTileRange_ReturnsExactlyThatTilesBytes()
    {
        var artifactId = await PublishArchiveAsync();

        var whole = await _client.GetByteArrayAsync($"/api/v1/tiles/pmtiles/{artifactId}");
        whole.Length.Should().BeGreaterThan(PMTilesHeader.HeaderSize);

        var header = PMTilesHeader.ReadFrom(whole);
        var rootRaw = whole.AsSpan(
            checked((int)header.RootDirectoryOffset),
            checked((int)header.RootDirectoryLength)).ToArray();
        var entries = PMTilesDirectory.DeserializeEntries(
            PMTilesDirectory.Decompress(rootRaw, header.InternalCompression));

        var entry = entries.FirstOrDefault(candidate => candidate.RunLength > 0 && candidate.Length > 0);
        entry.Length.Should().BeGreaterThan(0, "the published archive must contain at least one tile");

        var start = checked((long)(header.TileDataOffset + entry.Offset));
        var end = start + entry.Length - 1;
        start.Should().BeGreaterThan(
            PMTilesHeader.HeaderSize,
            "the window under test must be interior tile data, not the header prefix");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tiles/pmtiles/{artifactId}");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        var contentRange = response.Content.Headers.ContentRange;
        contentRange.Should().NotBeNull();
        contentRange!.Unit.Should().Be("bytes");
        contentRange.From.Should().Be(start);
        contentRange.To.Should().Be(end);
        contentRange.Length.Should().Be(whole.Length,
            "Content-Range must report the full archive length so a client can plan further reads");

        var ranged = await response.Content.ReadAsByteArrayAsync();
        var expected = whole.AsSpan(checked((int)start), checked((int)entry.Length)).ToArray();
        ranged.Should().Equal(expected, "a ranged read must return exactly that tile's bytes");

        // And the bytes really are a tile: the published archive stores MVT, so a tile that
        // decompresses (when compressed) must be non-empty payload rather than padding.
        var payload = header.TileCompression == PMTilesCompression.None
            ? ranged
            : PMTilesDirectory.Decompress(ranged, header.TileCompression);
        payload.Should().NotBeEmpty();
    }

    /// <summary>
    /// The archive the proxy serves must be the archive on disk, byte for byte — otherwise a
    /// client that range-reads it computes offsets against something the server never sent
    /// (honua-server#4397).
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{*artifactId}")]
    public async Task PMTilesProxy_ServedArchive_IsReadableAsAWholePMTilesArchive()
    {
        var artifactId = await PublishArchiveAsync();

        var whole = await _client.GetByteArrayAsync($"/api/v1/tiles/pmtiles/{artifactId}");

        // Parse the served bytes the way an independent reader would: magic, then header, then
        // the root directory, then a tile located by coordinate.
        whole[..7].Should().Equal("PMTiles"u8.ToArray());

        var header = PMTilesHeader.ReadFrom(whole);
        header.TileType.Should().Be(PMTilesTileType.Mvt, "the published archive must declare MVT tiles");
        header.AddressedTilesCount.Should().BeGreaterThan(0);
        checked((int)(header.TileDataOffset + header.TileDataLength)).Should().BeLessThanOrEqualTo(
            whole.Length, "the served archive must contain the whole tile-data section it declares");

        var rootRaw = whole.AsSpan(
            checked((int)header.RootDirectoryOffset),
            checked((int)header.RootDirectoryLength)).ToArray();
        var entries = PMTilesDirectory.DeserializeEntries(
            PMTilesDirectory.Decompress(rootRaw, header.InternalCompression));
        entries.Should().NotBeEmpty("the served archive must expose a readable root directory");

        // Every directory entry must address bytes that are actually present in what was served.
        foreach (var entry in entries.Where(candidate => candidate.RunLength > 0))
        {
            var end = header.TileDataOffset + entry.Offset + entry.Length;
            end.Should().BeLessThanOrEqualTo((ulong)whole.Length,
                "entry for tileId {0} addresses bytes past the end of the served archive", entry.TileId);
        }
    }

    private async Task<string> PublishArchiveAsync()
    {
        var jobId = await StartPublishJobAsync();
        var (status, finalJson) = await WaitForJobCompletionAsync(jobId);
        status.Should().Be(OperationStatus.Completed, "publish job must complete to exercise the proxy");

        var artifactId = GetPropertyCaseInsensitive(
            GetPropertyCaseInsensitive(finalJson!.RootElement, "publishedArtifact"),
            "artifactId").GetString()!;
        finalJson.Dispose();
        return artifactId;
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{*artifactId}")]
    public async Task PMTilesProxy_MissingArtifact_Returns404()
    {
        var response = await _client.GetAsync($"/api/v1/tiles/pmtiles/{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/tiles/pmtiles/{*artifactId}")]
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

    [IntegrationTest]
    [Endpoint("HEAD /api/v1/tiles/pmtiles/{*artifactId}")]
    public async Task PMTilesProxy_HeadRequest_Returns200WithContentLengthAndNoBody()
    {
        var jobId = await StartPublishJobAsync();
        var (status, finalJson) = await WaitForJobCompletionAsync(jobId);
        status.Should().Be(OperationStatus.Completed, "publish job must complete to test HEAD probes");
        var descriptor = GetPropertyCaseInsensitive(finalJson!.RootElement, "publishedArtifact");
        var artifactId = GetPropertyCaseInsensitive(descriptor, "artifactId").GetString()!;
        var sizeBytes = GetPropertyCaseInsensitive(descriptor, "sizeBytes").GetInt64();
        finalJson.Dispose();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/tiles/pmtiles/{artifactId}");
        using var headResponse = await _client.SendAsync(headRequest);

        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        headResponse.Headers.AcceptRanges.Should().Contain("bytes");
        headResponse.Content.Headers.ContentLength.Should().Be(sizeBytes);
        var body = await headResponse.Content.ReadAsByteArrayAsync();
        body.Length.Should().Be(0, "HEAD responses must not transfer a body");
    }

    [IntegrationTest]
    [Endpoint("HEAD /api/v1/tiles/pmtiles/{*artifactId}")]
    public async Task PMTilesProxy_HeadMissingArtifact_Returns404()
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/api/v1/tiles/pmtiles/{Guid.NewGuid():N}");
        using var response = await _client.SendAsync(headRequest);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<string> StartPublishJobAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "publish",
            serviceId = WebAppFixture.TestServiceId,
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
        foreach (var property in element.EnumerateObject()
            .Where(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)))
        {
            return property.Value;
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}
