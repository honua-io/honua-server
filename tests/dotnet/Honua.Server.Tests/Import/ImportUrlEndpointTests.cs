// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ImportUrlEndpointTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var geoJsonContent = """
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
                        "name": "Remote Test Point"
                    }
                }
            ]
        }
        """;

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(
                    new StubHttpClientFactory(
                        "https://s3.amazonaws.com/sample-bucket/test.geojson",
                        "application/json",
                        geoJsonContent));
            });
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_WithSupportedPublicS3Source_ReturnsPreview()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/sample-bucket/test.geojson"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoJson");
        content.Should().Contain("Remote Test Point");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    public async Task UploadUrl_WithSupportedPublicS3Source_ImportsData()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/upload-url",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/sample-bucket/test.geojson",
              "tableName": "remote_url_import_table",
              "overwriteExisting": true
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("remote_url_import_table");
        content.Should().Contain("GeoJson");
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class StubHttpClientFactory(string expectedUrl, string contentType, string body) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new StubHttpMessageHandler(expectedUrl, contentType, body));

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    private sealed class StubHttpMessageHandler(string expectedUrl, string contentType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.ToString().Should().Be(expectedUrl);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }
}
