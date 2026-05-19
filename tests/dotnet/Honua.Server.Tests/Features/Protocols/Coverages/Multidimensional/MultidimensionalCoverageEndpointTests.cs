// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Integration tests for the cloud-optimized HDF5 / NetCDF4 coverage admin
/// surface. The reader is intentionally not enabled in this MVP (see
/// ADR-0039); refresh is expected to return 501 with a stable problem code.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Cog)]
[Operation(Operations.CogAdmin)]
public class MultidimensionalCoverageEndpointTests : IAsyncLifetime
{
    private const string Route = "/api/v1/admin/multidim-coverages";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithMissingName_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "granule.nc4",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithMismatchedExtension_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "bad-ext",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "granule.tif",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(".nc");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithPathTraversal_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "traversal",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "../../etc/passwd.nc",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithLocalProvider_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "local",
            format = "NetCdf4",
            provider = "Local",
            bucket = "bucket",
            objectKey = "granule.nc4",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithMissingLayer_Returns404()
    {
        var request = new
        {
            layerId = 99999,
            name = "missing-layer",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "granule.nc4",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/multidim-coverages")]
    public async Task List_WithoutLayerId_Returns400()
    {
        var response = await _client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/multidim-coverages/{id}")]
    public async Task Get_WithNonexistentId_Returns404()
    {
        var response = await _client.GetAsync($"{Route}/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/multidim-coverages/{id}")]
    public async Task Delete_WithNonexistentId_Returns404()
    {
        var response = await _client.DeleteAsync($"{Route}/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Crud_RegisterListGetDelete_Lifecycle()
    {
        var request = new
        {
            layerId = 1,
            name = "lifecycle-multidim",
            description = "integration test",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "noaa-sst",
            objectKey = "ghrsst/2026/05/lifecycle.nc4",
            variables = new[] { "analysed_sst" }
        };

        var createResponse = await _client.PostAsJsonAsync(Route, request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);
        createDoc.RootElement.GetProperty("format").GetString().Should().Be("NetCdf4");
        createDoc.RootElement.GetProperty("status").GetString().Should().Be("reader-not-enabled");

        var listResponse = await _client.GetAsync($"{Route}?layerId=1");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listDoc.RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("id").GetInt64() == id);

        var getResponse = await _client.GetAsync($"{Route}/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getDoc.RootElement.GetProperty("objectKey").GetString().Should().Be("ghrsst/2026/05/lifecycle.nc4");
        getDoc.RootElement.GetProperty("variables").EnumerateArray()
            .Should().ContainSingle().Which.GetString().Should().Be("analysed_sst");

        var deleteResponse = await _client.DeleteAsync($"{Route}/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages/{id}/refresh")]
    public async Task Refresh_WithoutCloudCredentials_RejectsClientError()
    {
        // In the test environment no AwsS3 / AzureBlob range reader is wired
        // (no FileStorage credentials in test config). Refresh must reject
        // with a client error before any reader is invoked. The not-enabled
        // 501 path is exercised by NotEnabledMultidimensionalCoverageMetadataReaderTests
        // in Honua.Core.Tests.
        var request = new
        {
            layerId = 1,
            name = "refresh-no-credentials",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "noaa-sst",
            objectKey = "ghrsst/refresh-no-credentials.nc4",
            variables = Array.Empty<string>()
        };

        var createResponse = await _client.PostAsJsonAsync(Route, request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("id").GetInt64();

        try
        {
            var refreshResponse = await _client.PostAsync($"{Route}/{id}/refresh", null);

            ((int)refreshResponse.StatusCode).Should().BeInRange(400, 499);
        }
        finally
        {
            await _client.DeleteAsync($"{Route}/{id}");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages/{id}/refresh")]
    public async Task Refresh_WithNonexistentId_Returns404()
    {
        var response = await _client.PostAsync($"{Route}/99999/refresh", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
