// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class IdempotentUrlImportEndpointTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;
    private readonly ImportClientFactory _clients = new();

    public async Task InitializeAsync()
    {
        _fixture = new WebAppFixture().UseKestrel()
            .ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(_clients));
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        _clients.Dispose();
    }

    [IntegrationTheory]
    [InlineData("/api/v1/admin/import/upload-url", "/api/v1/admin/imports")]
    [InlineData("/api/v1/admin/imports", "/api/v1/admin/import/upload-url")]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    [Endpoint("POST /api/v1/admin/imports")]
    public async Task SameKeyAcrossRoutes_DownloadsAndPersistsOnlyOnce(string firstPath, string retryPath)
    {
        var key = Guid.NewGuid().ToString("N");
        var table = $"idem_{key[..12]}";
        var body = $$"""{"sourceUrl":"https://s3.amazonaws.com/sample-bucket/test.geojson","tableName":"{{table}}","overwriteExisting":false}""";
        using var first = await SendAsync(firstPath, key, body);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, firstBody);
        using var retry = await SendAsync(retryPath, key, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, _clients.Downloads);
        Assert.Equal(first.Headers.GetValues("X-Honua-Operation-Instance-Id"), retry.Headers.GetValues("X-Honua-Operation-Instance-Id"));
        Assert.Equal(first.Headers.GetValues("X-Honua-Audit-Id"), retry.Headers.GetValues("X-Honua-Audit-Id"));

        using var result = JsonDocument.Parse(firstBody);
        Assert.Equal(1, result.RootElement.GetProperty("featureCount").GetInt32());
        var schema = result.RootElement.GetProperty("schema").GetString()!;
        var physicalTable = result.RootElement.GetProperty("physicalTableName").GetString()!;
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        using var identifiers = new NpgsqlCommandBuilder();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM {identifiers.QuoteIdentifier(schema)}.{identifiers.QuoteIdentifier(physicalTable)}", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());

        using var conflict = await SendAsync(retryPath, key, body.Replace(table, table + "_other", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, _clients.Downloads);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key);
        return await _fixture.Client.SendAsync(request);
    }

    private sealed class ImportClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly DownloadHandler _download = new();
        private readonly HttpClient _loopback = new();
        private readonly HttpClient _source;
        public int Downloads => _download.Downloads;
        public ImportClientFactory() => _source = new HttpClient(_download);
        public HttpClient CreateClient(string name) => name == AdminConnectImportOperationExecutor.HttpClientName ? _loopback : _source;
        public void Dispose() { _source.Dispose(); _loopback.Dispose(); }
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        private int _downloads;
        public int Downloads => _downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://s3.amazonaws.com/sample-bucket/test.geojson", request.RequestUri!.AbsoluteUri);
            Interlocked.Increment(ref _downloads);
            const string geoJson = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-157.8583,21.3069]},"properties":{"name":"one import"}}]}""";
            return Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(geoJson, Encoding.UTF8, "application/geo+json"),
            });
        }
    }
}
