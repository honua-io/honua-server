// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Integration tests for the OGC SensorThings API (STA v1.1) Phase 3 real-time observation
/// stream (#1747): the SSE transport handshake and live push of a newly-ingested observation.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsStreamEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_WithoutTransportHeader_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/ObservationsStream");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_Sse_EmitsConnectedThenPushesIngestedObservation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/sta/v1.1/ObservationsStream?datastreamId=1");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        using var response = await _fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // The handshake emits a "connected" status frame before any observation.
        var handshake = await ReadUntilAsync(reader, "event: status", cts.Token);
        handshake.Should().Contain("connected");

        // Ingest an observation on datastream 1; the stream must push it.
        var ingest = await _fixture.Client.PostAsync(
            "/sta/v1.1/Datastreams(1)/Observations",
            JsonContent.Create(new { result = 99.0 }),
            cts.Token);
        ingest.StatusCode.Should().Be(HttpStatusCode.Created);

        var pushed = await ReadUntilAsync(reader, "event: observation", cts.Token);
        pushed.Should().Contain("\"result\":99");
        pushed.Should().Contain("\"datastreamId\":1");
    }

    /// <summary>
    /// Reads SSE lines until a line containing <paramref name="marker"/> is seen, then returns
    /// the marker line plus the following data line. Heartbeats are skipped.
    /// </summary>
    private static async Task<string> ReadUntilAsync(StreamReader reader, string marker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException($"Stream closed before '{marker}' was observed.");
            }

            if (line.Contains(marker, StringComparison.Ordinal))
            {
                var dataLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                return string.Concat(line, "\n", dataLine);
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }
}
