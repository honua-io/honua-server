// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Isolated diagnostic replay of the existing maximum-size collection request.
/// </summary>
[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public sealed class OgcMapsMaximumDimensionsDiagnosticTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ITestOutputHelper _output;

    public OgcMapsMaximumDimensionsDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_ => new DiagnosticLogProvider(_messages)));
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_MaximumDimensions_ReturnsSuccessWithDiagnosticEvidence()
    {
        const string path = "/ogc/maps/collections/0/map?width=4096&height=4096&bbox=-180,-90,180,90&f=png";
        _output.WriteLine("Request: {0}", path);
        try
        {
            using var response = await _fixture.Client.GetAsync(path);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            _output.WriteLine("Status: {0}; Content-Type: {1}; bytes: {2}; SHA256: {3}",
                response.StatusCode, response.Content.Headers.ContentType, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)));
            if (!response.IsSuccessStatusCode)
            {
                var retained = Math.Min(bytes.Length, 65536);
                _output.WriteLine("Error body (retained {0}/{1} bytes): {2}",
                    retained, bytes.Length, Encoding.UTF8.GetString(bytes, 0, retained));
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "the original 4096 by 4096 request must still succeed; retained response and logs explain any failure");
        }
        finally
        {
            _output.WriteLine("Captured {0} relevant warning/error records (maximum 100)", _messages.Count);
            foreach (var message in _messages)
            {
                _output.WriteLine(message);
            }
        }
    }

    private sealed class DiagnosticLogProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(categoryName, messages);
        public void Dispose() { }
    }

    private sealed class DiagnosticLogger(string category, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning &&
            (category.Contains(".Maps.", StringComparison.Ordinal) ||
             category.Contains(".Rendering.", StringComparison.Ordinal) ||
             category.Contains(".Raster.", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || messages.Count >= 100)
            {
                return;
            }

            var text = $"{category} {logLevel} {eventId}: {formatter(state, exception)}\n{exception}";
            messages.Enqueue(text.Length <= 8192 ? text : text[..8192] + " [truncated at 8192 characters]");
        }
    }
}
