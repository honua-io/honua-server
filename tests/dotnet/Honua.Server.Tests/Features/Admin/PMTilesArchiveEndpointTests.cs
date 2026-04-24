// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class PMTilesArchiveEndpointTests : IAsyncLifetime
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
    public async Task StartJob_ArchiveOperation_ReturnsAccepted()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "archive",
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
    public async Task StartJob_ArchiveOperationWithoutLayer_ReturnsBadRequest()
    {
        var content = new StringContent(
            """{"operation":"archive"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await _client.PostAsync("/api/v1/admin/tile-operations/jobs", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs/{jobId}")]
    public async Task GetJobStatus_ArchiveJob_ReturnsProgressWithArchiveFields()
    {
        var jobId = await StartArchiveJobAsync();
        var (finalStatus, lastJson) = await WaitForJobCompletionAsync(jobId);

        finalStatus.Should().NotBeNull("job should have completed within timeout");

        var root = lastJson!.RootElement;
        GetPropertyCaseInsensitive(root, "operation").GetString().Should().Be("archive");

        var archiveSize = GetPropertyCaseInsensitive(root, "archiveSizeBytes").GetInt64();

        if (finalStatus == OperationStatus.Completed)
        {
            archiveSize.Should().BeGreaterThan(0, "completed archive should have non-zero size");
            GetPropertyCaseInsensitive(root, "archiveFileId").GetString().Should().NotBeNullOrWhiteSpace();
        }

        lastJson.Dispose();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs")]
    public async Task ListJobs_WithArchiveJob_IncludesArchiveInList()
    {
        await StartArchiveJobAsync();

        var response = await _client.GetAsync("/api/v1/admin/tile-operations/jobs?activeOnly=false");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobs = GetPropertyCaseInsensitive(json.RootElement, "jobs");
        jobs.ValueKind.Should().Be(JsonValueKind.Array);
        jobs.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/tile-operations/jobs/{jobId}")]
    public async Task CompletedArchiveJob_ProducesValidPMTilesArchive()
    {
        var jobId = await StartArchiveJobAsync();
        var (finalStatus, lastJson) = await WaitForJobCompletionAsync(jobId);

        finalStatus.Should().Be(OperationStatus.Completed,
            "archive job must complete successfully to validate the archive binary");

        var root = lastJson!.RootElement;
        var archiveFileId = GetPropertyCaseInsensitive(root, "archiveFileId").GetString();
        archiveFileId.Should().NotBeNullOrWhiteSpace();
        lastJson.Dispose();

        // Download archive bytes from cloud storage
        var cloudStorage = _fixture.GetOptionalService<ICloudFileStorage>();
        cloudStorage.Should().NotBeNull("test host should register LocalFileStorage");

        var archiveBytes = await cloudStorage!.DownloadBytesAsync(archiveFileId!);
        archiveBytes.Should().NotBeNull("archive file should exist in storage");
        archiveBytes!.Length.Should().BeGreaterThan(127,
            "archive should be larger than just a header");

        // Validate PMTiles v3 magic bytes and version
        archiveBytes[..7].Should().BeEquivalentTo("PMTiles"u8.ToArray(),
            "archive must start with PMTiles magic bytes");
        archiveBytes[7].Should().Be(3, "archive must be PMTiles v3");

        // Parse header fields at known offsets (PMTiles v3 binary layout)
        var rootDirOffset = BitConverter.ToUInt64(archiveBytes, 8);
        var rootDirLength = BitConverter.ToUInt64(archiveBytes, 16);
        var jsonMetaOffset = BitConverter.ToUInt64(archiveBytes, 24);
        var jsonMetaLength = BitConverter.ToUInt64(archiveBytes, 32);
        var leafDirOffset = BitConverter.ToUInt64(archiveBytes, 40);
        var leafDirLength = BitConverter.ToUInt64(archiveBytes, 48);
        var tileDataOffset = BitConverter.ToUInt64(archiveBytes, 56);
        var tileDataLength = BitConverter.ToUInt64(archiveBytes, 64);
        var addressedTiles = BitConverter.ToUInt64(archiveBytes, 72);
        var tileEntries = BitConverter.ToUInt64(archiveBytes, 80);
        var clustered = archiveBytes[96];
        var tileType = archiveBytes[99];

        // Validate header semantics
        tileType.Should().Be(1, "tile type should be MVT (1)");
        addressedTiles.Should().BeGreaterThan(0, "archive should contain at least one tile");
        tileEntries.Should().BeGreaterThan(0);
        clustered.Should().Be(1, "archive should be clustered");

        // Validate archive layout: sections must be contiguous
        rootDirOffset.Should().Be(127, "root directory should start immediately after header");
        jsonMetaOffset.Should().Be(rootDirOffset + rootDirLength);
        leafDirOffset.Should().Be(jsonMetaOffset + jsonMetaLength);
        tileDataOffset.Should().Be(leafDirOffset + leafDirLength);

        // Validate total size matches archive byte length
        var totalSize = tileDataOffset + tileDataLength;
        ((ulong)archiveBytes.Length).Should().Be(totalSize,
            "archive byte length must match header-declared total size");

        // Validate tile data section is non-empty and within bounds
        tileDataLength.Should().BeGreaterThan(0, "tile data section should not be empty");
        (tileDataOffset + tileDataLength).Should().Be((ulong)archiveBytes.Length,
            "tile data should extend to end of archive");
    }

    private async Task<string> StartArchiveJobAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/tile-operations/jobs", new
        {
            operation = "archive",
            layerId = WebAppFixture.TestLayerId,
            minZoom = 0,
            maxZoom = 1,
            maxTiles = 5
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return GetPropertyCaseInsensitive(json.RootElement, "jobId").GetString()!;
    }

    private async Task<(OperationStatus? Status, JsonDocument? Json)> WaitForJobCompletionAsync(
        string jobId,
        int timeoutSeconds = 30)
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
