// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
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
public sealed class GeoJsonPreviewValidationTests : IAsyncLifetime
{
    private const string StubUrl = "https://s3.amazonaws.com/sample-bucket/invalid.geojson";
    private const string TruncatedUrl = "https://s3.amazonaws.com/sample-bucket/truncated.geojson";
    private const string MalformedGeoJson = "{not-json";
    private const string TruncatedGeoJson = "{\"type\":\"FeatureCollecti";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(_ => new MalformedGeoJsonClientFactory()));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(MalformedGeoJson)]
    [InlineData(TruncatedGeoJson)]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_MalformedGeoJson_ReturnsBadRequest(string json)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StringContent(json, Encoding.UTF8, "application/geo+json");
        content.Add(fileContent, "file", "invalid.geojson");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _fixture.Client.PostAsync("/api/v1/admin/import/preview", content, timeout.Token);

        await AssertMalformedFileRefusalAsync(response);
    }

    [IntegrationTheory]
    [InlineData(StubUrl)]
    [InlineData(TruncatedUrl)]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_MalformedGeoJson_ReturnsBadRequest(string sourceUrl)
    {
        using var content = new StringContent(
            $$"""{"sourceUrl":"{{sourceUrl}}"}""", Encoding.UTF8, "application/json");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _fixture.Client.PostAsync("/api/v1/admin/import/preview-url", content, timeout.Token);

        await AssertMalformedFileRefusalAsync(response);
    }

    private static async Task AssertMalformedFileRefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Failed to preview file");
        body.Should().NotContain("JsonException").And.NotContain("BytePositionInLine").And.NotContain("StackTrace");
    }

    private sealed class MalformedGeoJsonClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new MalformedGeoJsonHandler());

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class MalformedGeoJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.Should().NotBeNull();
            var sourceUrl = request.RequestUri!.ToString();
            sourceUrl.Should().BeOneOf(StubUrl, TruncatedUrl);
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sourceUrl == TruncatedUrl ? TruncatedGeoJson : MalformedGeoJson,
                    Encoding.UTF8, "application/geo+json")
            });
        }
    }
}
