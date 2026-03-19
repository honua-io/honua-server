// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for GeoParquet preview-url error handling.
/// Verifies that invalid GeoParquet metadata returns 400 instead of 500.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoParquetUrlPreviewTests : IAsyncLifetime
{
    private const string StubUrl = "https://s3.amazonaws.com/sample-bucket/test.parquet";

    private WebAppFixture _fixture = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Create a valid Parquet file that is missing the required "geo" metadata key.
        // This triggers InvalidDataException in GeoParquetReader.ParseGeoMetadata.
        await using var stream = await GeoParquetTestFactory.CreateWithoutGeoMetadataAsync();
        var parquetBytes = stream.ToArray();

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(
                    new BinaryStubHttpClientFactory(
                        StubUrl,
                        "application/vnd.apache.parquet",
                        parquetBytes));
            });
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_WithMissingGeoMetadata_Returns400()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent($$"""
            {
              "sourceUrl": "{{StubUrl}}"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Failed to preview file");
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class BinaryStubHttpClientFactory(string expectedUrl, string contentType, byte[] body) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new BinaryStubHttpMessageHandler(expectedUrl, contentType, body));

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    private sealed class BinaryStubHttpMessageHandler(string expectedUrl, string contentType, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.ToString().Should().Be(expectedUrl);

            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }
}
