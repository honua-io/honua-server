// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ProviderOnlyServiceSettingsTests : IAsyncLifetime
{
    private const string AdminPassword = "provider-only-settings-admin-key";
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ProviderOnlyServiceSettingsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            })
            .ConfigureServices(services => services.RemoveAll<IMetadataV2GraphStore>());
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/settings")]
    public async Task GetServiceSettings_WithoutAGraphStore_ReturnsTheProviderSnapshot()
    {
        _fixture.Services.GetService<IMetadataV2GraphStore>().Should().BeNull(
            "this host has a metadata provider and no graph store");

        var response = await _client.GetAsync("/api/v1/admin/services/test/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("serviceName").GetString().Should().Be("test");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/folders")]
    [Endpoint("POST /api/v1/admin/folders")]
    public async Task Folders_WithoutGraphStore_ListReadsAndMutationReturnsUnavailable()
    {
        var listed = await _client.GetAsync("/api/v1/admin/folders");
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        listed.Headers.ETag.Should().NotBeNull();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", listed.Headers.ETag!.ToString());
        var mutation = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        mutation.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/mapserver")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/timeinfo")]
    public async Task UpdateMapServerAndTimeInfo_WithCrLfServiceName_SanitizesLogOutput()
    {
        var mapServerResponse = await _client.PutAsJsonAsync(
            "/api/v1/admin/services/nosvc%0d%0a%5BERR%5D%20FORGED_MAPSERVER/mapserver",
            new { maxImageWidth = 2048 });
        mapServerResponse.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        var timeInfoResponse = await _client.PutAsJsonAsync(
            "/api/v1/admin/services/nosvc%0d%0a%5BERR%5D%20FORGED_TIMEINFO/timeinfo",
            new { startTimeField = "start_time" });
        timeInfoResponse.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        var messages = _logs.Messages.ToArray();
        messages.Should().Contain(m => m.Contains("nosvc__[ERR] FORGED_MAPSERVER", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains("nosvc__[ERR] FORGED_TIMEINFO", StringComparison.Ordinal));
        messages.Where(m => m.Contains("FORGED_MAPSERVER", StringComparison.Ordinal)
                || m.Contains("FORGED_TIMEINFO", StringComparison.Ordinal))
            .Should().OnlyContain(m => !m.Contains('\r') && !m.Contains('\n'));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }
}
