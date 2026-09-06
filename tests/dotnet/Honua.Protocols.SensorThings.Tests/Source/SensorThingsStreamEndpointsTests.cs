// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Integration tests for the OGC SensorThings API (STA v1.1) Phase 3 real-time observation
/// stream (#1747): the SSE transport handshake and live push of a newly-ingested observation.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed partial class SensorThingsStreamEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["MultiTenancy:MultiTenantAdminRoles:0"] = "admin" }));
    });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_WithoutTransportHeader_Returns400()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var response = await adminClient.GetAsync("/sta/v1.1/ObservationsStream");

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

        using var streamClient = _fixture.CreateAdminClient();
        using var response = await streamClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // The handshake emits a "connected" status frame before any observation.
        var handshake = await ReadUntilAsync(reader, "event: status", cts.Token);
        handshake.Should().Contain("connected");

        // Ingest an observation on datastream 1; the stream must push it.
        using var adminClient = _fixture.CreateAdminClient();
        var ingest = await adminClient.PostAsync(
            "/sta/v1.1/Datastreams(1)/Observations",
            JsonContent.Create(new { result = 99.0 }),
            cts.Token);
        ingest.StatusCode.Should().Be(HttpStatusCode.Created);

        var pushed = await ReadUntilAsync(reader, "event: observation", cts.Token);
        pushed.Should().Contain("\"result\":99");
        pushed.Should().Contain("\"datastreamId\":1");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_Anonymous_IsDeniedBeforeHandshake(bool webSocket)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sta/v1.1/ObservationsStream");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (webSocket)
        {
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        }

        using var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_CollidingTenantDatastreams_OnlyDeliversOwnPersistedValues()
    {
        var otherSchema = await _fixture.Postgres.CreateIsolatedSchemaAsync("sta_other_tenant");
        try
        {
            await ServerTestData.SeedAsync(_fixture.Postgres, otherSchema);
            using var tenantA = _fixture.CreateAdminClient();
            tenantA.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-a");
            using var tenantB = _fixture.CreateAdminClient();
            tenantB.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-b");
            tenantB.DefaultRequestHeaders.Remove("X-Honua-Test-Schema");
            tenantB.DefaultRequestHeaders.Add("X-Honua-Test-Schema", otherSchema);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/sta/v1.1/ObservationsStream?datastreamId=1");
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            using var response = await tenantB.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            (await ReadUntilAsync(reader, "event: status", cts.Token)).Should().Contain("connected");

            // Both schemas seed Datastream(1). A's committed value must never precede B's
            // sentinel on B's ordered local queue; this detects leakage without a timing sleep.
            using var first = await tenantA.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations",
                new { result = 42.5, phenomenonTime = "2026-09-05T01:02:03Z" }, cts.Token);
            first.StatusCode.Should().Be(HttpStatusCode.Created);
            using var second = await tenantB.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations",
                new { result = 81.25, phenomenonTime = "2026-09-05T04:05:06Z" }, cts.Token);
            second.StatusCode.Should().Be(HttpStatusCode.Created);

            var pushed = await ReadUntilAsync(reader, "event: observation", cts.Token);
            using var frame = JsonDocument.Parse(pushed[(pushed.IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
            frame.RootElement.GetProperty("result").GetDouble().Should().Be(81.25);
            frame.RootElement.GetProperty("datastreamId").GetInt64().Should().Be(1);
            frame.RootElement.GetProperty("phenomenonTime").GetString().Should().Be("2026-09-05T04:05:06.000Z");
            using var persisted = JsonDocument.Parse(await second.Content.ReadAsStringAsync(cts.Token));
            frame.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(persisted.RootElement.GetProperty("@iot.id").GetInt64());
        }
        finally
        {
            await _fixture.Postgres.DropSchemaAsync(otherSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_WebSocket_OnlyDeliversResolvedTenant()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = _fixture.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["X-API-Key"] = WebAppFixture.SharedAdminPassword;
            request.Headers["X-Honua-Test-Schema"] = _fixture.CurrentSchema;
            request.Headers["X-Honua-Tenant"] = "tenant-b";
        };
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/sta/v1.1/ObservationsStream"), cts.Token);
        using var connected = await ReadSocketFrameAsync(socket, cts.Token);
        connected.RootElement.GetProperty("status").GetString().Should().Be("connected");
        using var tenantA = _fixture.CreateAdminClient();
        tenantA.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-a");
        using var tenantB = _fixture.CreateAdminClient();
        tenantB.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-b");
        using var first = await tenantA.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations",
            new { result = 42.5 }, cts.Token);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        using var second = await tenantB.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations",
            new { result = 81.25, phenomenonTime = "2026-09-05T04:05:06Z" }, cts.Token);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        using var frame = await ReadSocketFrameAsync(socket, cts.Token);
        frame.RootElement.GetProperty("result").GetDouble().Should().Be(81.25);
        frame.RootElement.GetProperty("datastreamId").GetInt64().Should().Be(1);
        frame.RootElement.GetProperty("phenomenonTime").GetString().Should().Be("2026-09-05T04:05:06.000Z");
        using var persisted = JsonDocument.Parse(await second.Content.ReadAsStringAsync(cts.Token));
        frame.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(persisted.RootElement.GetProperty("@iot.id").GetInt64());
    }

    private static async Task<JsonDocument> ReadSocketFrameAsync(WebSocket socket, CancellationToken ct)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            result.MessageType.Should().Be(WebSocketMessageType.Text);
            payload.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(payload.ToArray());
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
