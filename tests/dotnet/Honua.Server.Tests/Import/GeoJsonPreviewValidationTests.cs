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
    private const string MalformedGeoJson = "{not-json";
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(_ => new MalformedGeoJsonClientFactory()));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_MalformedGeoJson_ReturnsBadRequest()
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StringContent(MalformedGeoJson, Encoding.UTF8, "application/geo+json");
        content.Add(fileContent, "file", "invalid.geojson");

        using var response = await _fixture.Client.PostAsync("/api/v1/admin/import/preview", content);

        await AssertMalformedFileRefusalAsync(response);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_MalformedGeoJson_ReturnsBadRequest()
    {
        using var content = new StringContent(
            $$"""{"sourceUrl":"{{StubUrl}}"}""", Encoding.UTF8, "application/json");

        using var response = await _fixture.Client.PostAsync("/api/v1/admin/import/preview-url", content);

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
            request.RequestUri!.ToString().Should().Be(StubUrl);
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MalformedGeoJson, Encoding.UTF8, "application/geo+json")
            });
        }
    }
}
