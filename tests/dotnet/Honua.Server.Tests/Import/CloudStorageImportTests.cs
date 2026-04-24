// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Tests for URL-based import workflows targeting S3 and Azure Blob cloud storage hosts.
/// Uses StubHttpClientFactory to mock HTTP responses from cloud URLs.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class CloudStorageImportTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;
    private HttpClient _client = null!;

    private static readonly string GeoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Point",
                        "coordinates": [-122.4194, 37.7749]
                    },
                    "properties": {
                        "name": "Cloud Import Test Point"
                    }
                }
            ]
        }
        """;

    private static readonly byte[] GeoJsonBytes = Encoding.UTF8.GetBytes(GeoJsonContent);

    public async Task InitializeAsync()
    {
        var fileGdbBytes = LoadTestData("TestData", "FileGdb", "testopenfilegdb.gdb.zip");
        var shapefileBytes = LoadTestData("TestData", "Extreme_Tsunami_Evacuation_Zones.zip");

        var responses = new Dictionary<string, (string contentType, byte[] body)>
        {
            ["https://s3.amazonaws.com/bucket/test.geojson"] =
                ("application/json", GeoJsonBytes),
            ["https://s3.amazonaws.com/bucket/test.gdb.zip"] =
                ("application/zip", fileGdbBytes),
            ["https://s3.amazonaws.com/bucket/zones.zip"] =
                ("application/zip", shapefileBytes)
        };

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(
                    new MultiUrlStubHttpClientFactory(responses));
            });
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_GeoJsonFromS3_ReturnsPreview()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/bucket/test.geojson"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoJson");
        content.Should().Contain("Cloud Import Test Point");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_FileGdbFromS3_ReturnsPreview()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/bucket/test.gdb.zip",
              "fileName": "test.gdb.zip"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("FileGdb");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    public async Task UploadUrl_GeoJsonFromS3_ImportsData()
    {
        var tableName = $"s3_geojson_{Guid.NewGuid().ToString("N")[..8]}";

        using var response = await _client.PostAsync(
            "/api/v1/admin/import/upload-url",
            JsonContent($$"""
            {
              "sourceUrl": "https://s3.amazonaws.com/bucket/test.geojson",
              "tableName": "{{tableName}}",
              "overwriteExisting": true
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(tableName);
        content.Should().Contain("GeoJson");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    public async Task UploadUrl_MultiLayerFileGdbFromS3_FailsWithoutMergingLayers()
    {
        var tableName = $"s3_fgdb_{Guid.NewGuid().ToString("N")[..8]}";

        using var response = await _client.PostAsync(
            "/api/v1/admin/import/upload-url",
            JsonContent($$"""
            {
              "sourceUrl": "https://s3.amazonaws.com/bucket/test.gdb.zip",
              "tableName": "{{tableName}}",
              "fileName": "test.gdb.zip",
              "targetSrid": 4326,
              "overwriteExisting": true
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(tableName);
        content.Should().Contain("FileGdb");
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorMessage").GetString().Should().Contain("multiple feature classes");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    public async Task UploadUrl_ShapefileFromS3_ImportsData()
    {
        var tableName = $"s3_shp_{Guid.NewGuid().ToString("N")[..8]}";

        using var response = await _client.PostAsync(
            "/api/v1/admin/import/upload-url",
            JsonContent($$"""
            {
              "sourceUrl": "https://s3.amazonaws.com/bucket/zones.zip",
              "tableName": "{{tableName}}",
              "overwriteExisting": true
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(tableName);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_UnsupportedHost_Returns400()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
              "sourceUrl": "https://evil.example.com/file.geojson"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_HttpUrl_Returns400()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
              "sourceUrl": "http://s3.amazonaws.com/bucket/test.geojson"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_MissingSourceUrl_Returns400()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static byte[] LoadTestData(params string[] pathSegments)
    {
        var path = Path.Combine(
            new[] { AppContext.BaseDirectory }.Concat(pathSegments).ToArray());
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Test data file not found: {path}");
        }

        return File.ReadAllBytes(path);
    }

    private sealed class MultiUrlStubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public MultiUrlStubHttpClientFactory(Dictionary<string, (string contentType, byte[] body)> responses)
        {
            _client = new HttpClient(new MultiUrlStubHandler(responses));
        }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class MultiUrlStubHandler(
        Dictionary<string, (string contentType, byte[] body)> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (responses.TryGetValue(url, out var resp))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(resp.body)
                    {
                        Headers =
                        {
                            ContentType =
                                new System.Net.Http.Headers.MediaTypeHeaderValue(resp.contentType)
                        }
                    }
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
