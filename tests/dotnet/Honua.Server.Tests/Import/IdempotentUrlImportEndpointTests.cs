// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
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
            .ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(_clients))
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
        await _fixture.InitializeAsync();
        _fixture.Client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
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

    [IntegrationTheory]
    [InlineData("/api/v1/admin/import/upload-url", "/api/v1/admin/imports")]
    [InlineData("/api/v1/admin/imports", "/api/v1/admin/import/upload-url")]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    [Endpoint("POST /api/v1/admin/imports")]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task BackgroundSameKeyAcrossRoutes_ReplaysOneCompletedJobAndPersistedFeature(string firstPath, string retryPath)
    {
        var key = Guid.NewGuid().ToString("N");
        var table = $"bgidem_{key[..12]}";
        var body = $$"""{"sourceUrl":"https://s3.amazonaws.com/sample-bucket/test.geojson","fileName":"test.geojson","tableName":"{{table}}","overwriteExisting":false,"forceBackground":true,"trackProgress":true}""";
        _clients.HoldDownload();
        var accepting = SendAsync(firstPath, key, body);
        try
        {
            await _clients.DownloadStarted.WaitAsync(TimeSpan.FromSeconds(30));
            using var concurrent = await SendAsync(retryPath, key, body);
            Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);
            Assert.Equal(1, _clients.Downloads);
        }
        finally
        {
            _clients.ReleaseDownload();
        }

        using var first = await accepting;
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.Accepted, firstBody);
        using var accepted = JsonDocument.Parse(firstBody);
        var jobId = accepted.RootElement.GetProperty("jobId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        using var retry = await SendAsync(retryPath, key, body);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.GetValues("X-Honua-Operation-Instance-Id"), retry.Headers.GetValues("X-Honua-Operation-Instance-Id"));
        Assert.Equal(first.Headers.GetValues("X-Honua-Audit-Id"), retry.Headers.GetValues("X-Honua-Audit-Id"));

        await AssertCompletedImportAsync(jobId, table, first);
        using var completedRetry = await SendAsync(firstPath, key, body);
        Assert.Equal(HttpStatusCode.Accepted, completedRetry.StatusCode);
        Assert.Equal(firstBody, await completedRetry.Content.ReadAsStringAsync());
        using var conflict = await SendAsync(retryPath, key, body.Replace(table, table + "_changed", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var readerKey = (await keys.CreateAsync($"bg-reader-{key}", ["admin:read"],
            DateTimeOffset.UtcNow.AddMinutes(5), "background-idempotency-test", CancellationToken.None)).Key;
        using var reader = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", readerKey));
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, retryPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        deniedRequest.Headers.Add("Idempotency-Key", key);
        using var denied = await reader.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain(jobId, await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, _clients.Downloads);
        await AssertCompletedImportAsync(jobId, table, first);

        // A second authorized actor owns a distinct operation even when the key matches.
        // Use a separate table so this intentional new import cannot alter the first result.
        var writerKey = (await keys.CreateAsync($"bg-writer-{key}", ["admin:write"],
            DateTimeOffset.UtcNow.AddMinutes(5), "background-idempotency-test", CancellationToken.None)).Key;
        using var writer = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", writerKey));
        var otherTable = table + "_other";
        using var otherRequest = new HttpRequestMessage(HttpMethod.Post, retryPath)
        {
            Content = new StringContent(body.Replace(table, otherTable, StringComparison.Ordinal), Encoding.UTF8, "application/json")
        };
        otherRequest.Headers.Add("Idempotency-Key", key);
        using var other = await writer.SendAsync(otherRequest);
        var otherBody = await other.Content.ReadAsStringAsync();
        Assert.True(other.StatusCode == HttpStatusCode.Accepted, otherBody);
        using var otherAccepted = JsonDocument.Parse(otherBody);
        var otherJobId = otherAccepted.RootElement.GetProperty("jobId").GetString()!;
        Assert.NotEqual(jobId, otherJobId);
        Assert.NotEqual(first.Headers.GetValues("X-Honua-Operation-Instance-Id").Single(),
            other.Headers.GetValues("X-Honua-Operation-Instance-Id").Single());
        await AssertCompletedImportAsync(otherJobId, otherTable, other);
        Assert.Equal(2, _clients.Downloads);
        await AssertCompletedImportAsync(jobId, table, first);
    }

    private async Task AssertCompletedImportAsync(string jobId, string table, HttpResponseMessage acceptedResponse)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            using var response = await _fixture.Client.GetAsync($"/api/v1/admin/import/jobs/{jobId}", timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var progress = JsonDocument.Parse(body);
            var root = progress.RootElement;
            var value = root.GetProperty("status");
            var status = value.ValueKind == JsonValueKind.String
                ? Enum.Parse<ImportStatus>(value.GetString()!, ignoreCase: true)
                : (ImportStatus)value.GetInt32();
            Assert.True(status is not (ImportStatus.Failed or ImportStatus.Cancelled), body);
            if (status == ImportStatus.Completed)
            {
                Assert.Equal(1, root.GetProperty("featuresProcessed").GetInt32());
                Assert.Equal(0, root.GetProperty("failedFeatures").GetInt32());
                Assert.Equal(table, root.GetProperty("tableName").GetString());
                Assert.Equal(acceptedResponse.Headers.GetValues("X-Honua-Operation-Instance-Id").Single(),
                    root.GetProperty("operationInstanceId").GetString());
                Assert.Equal(acceptedResponse.Headers.GetValues("X-Honua-Audit-Id").Single(),
                    root.GetProperty("auditId").GetString());
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }

        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync(timeout.Token);
        using var identifiers = new NpgsqlCommandBuilder();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM honua_data.{identifiers.QuoteIdentifier("imported_" + table)}", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync(timeout.Token));
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
        public Task DownloadStarted => _download.Started.Task;
        public void HoldDownload() => _download.Hold = true;
        public void ReleaseDownload() => _download.Release.TrySetResult();
        public ImportClientFactory() => _source = new HttpClient(_download);
        public HttpClient CreateClient(string name) => name == AdminConnectImportOperationExecutor.HttpClientName ? _loopback : _source;
        public void Dispose() { _source.Dispose(); _loopback.Dispose(); }
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        private int _downloads;
        public int Downloads => _downloads;
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://s3.amazonaws.com/sample-bucket/test.geojson", request.RequestUri!.AbsoluteUri);
            Interlocked.Increment(ref _downloads);
            Started.TrySetResult();
            if (Hold)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            const string geoJson = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[-157.8583,21.3069]},"properties":{"name":"one import"}}]}""";
            return new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(geoJson, Encoding.UTF8, "application/geo+json"),
            };
        }
    }
}
