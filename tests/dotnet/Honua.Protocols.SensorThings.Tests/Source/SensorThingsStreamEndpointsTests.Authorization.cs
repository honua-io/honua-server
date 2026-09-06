// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed partial class SensorThingsStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_UnscopedCredentialOrForeignCursor_IsRejectedBeforeHandshake(bool foreignCursor)
    {
        var issuer = _fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://observation-proof.example/";
        var credential = await issuer.IssueAsync(new PortalTokenIssueRequest("scope-proof", "Scope proof",
            foreignCursor ? "tenant-b" : null, ["reader"], PortalTokenClientType.Referer, referer,
            DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/sta/v1.1/ObservationsStream?datastreamId=1&token={credential.Token}" + (foreignCursor ? "&cursor=73001" : ""));
        request.Headers.Referrer = new Uri(referer);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(foreignCursor ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("tenant-a").And.NotContain("connected");
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_RealPortalCredential_TerminatesOnExpiryOrRevocation(bool webSocket, bool expire)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = timeout.Token;
        var issuer = _fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://observation-proof.example/";
        async Task<PortalTokenIssuance> IssueAsync(bool longLived = false) => await issuer.IssueAsync(
            new PortalTokenIssueRequest("sta-proof", "STA proof", "tenant-a", ["reader"],
                PortalTokenClientType.Referer, referer,
                DateTimeOffset.UtcNow.AddSeconds(expire && !longLived ? 45 : 120)), ct);
        PortalTokenIssuance credential;
        string Path(string token) => $"/sta/v1.1/ObservationsStream?datastreamId=1&token={token}";
        using var tenantA = _fixture.CreateAdminClient();
        tenantA.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-a");
        using var tenantB = _fixture.CreateAdminClient();
        tenantB.DefaultRequestHeaders.Add("X-Honua-Tenant", "tenant-b");
        async Task<long> PublishAsync(double value)
        {
            using var excluded = await tenantB.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations", new { result = -73001.5 }, ct);
            excluded.StatusCode.Should().Be(HttpStatusCode.Created);
            using var included = await tenantA.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations", new { result = value }, ct);
            included.StatusCode.Should().Be(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await included.Content.ReadAsStringAsync(ct));
            return body.RootElement.GetProperty("@iot.id").GetInt64();
        }
        async Task<WebSocket> ConnectAsync(string token)
        {
            var client = _fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request =>
            {
                configure?.Invoke(request);
                request.Headers.Referer = referer;
                request.Headers["X-Honua-Test-Schema"] = _fixture.CurrentSchema;
            };
            return await client.ConnectAsync(new Uri("ws://localhost" + Path(token)), ct);
        }
        async Task InvalidateAsync()
        {
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
        }
        async Task<JsonDocument> ReadObservationAsync(WebSocket socket)
        {
            while (true)
            {
                var frame = await ReadSocketFrameAsync(socket, ct);
                if (frame.RootElement.TryGetProperty("result", out _)) { return frame; }
                frame.RootElement.TryGetProperty("code", out _).Should().BeFalse("authorization cannot end before the marked observation");
                frame.RootElement.GetRawText().Should().NotContain("tenant-b");
                frame.Dispose();
            }
        }
        // Warm real ingestion/authentication paths before starting the credential's
        // clock; startup/JIT latency is not part of a connected expiry scenario.
        _ = await PublishAsync(0.125);
        credential = await IssueAsync();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void SetBound() => bound.CancelAfter(expire
            ? credential.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(5));

        if (webSocket)
        {
            using var socket = await ConnectAsync(credential.Token);
            using var connected = await ReadSocketFrameAsync(socket, ct);
            connected.RootElement.GetProperty("status").GetString().Should().Be("connected");
            var expectedId = await PublishAsync(73.25);
            using var observation = await ReadObservationAsync(socket);
            observation.RootElement.GetProperty("result").GetDouble().Should().Be(73.25);
            observation.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(expectedId);
            await InvalidateAsync();
            SetBound();
            var buffer = new byte[4096];
            WebSocketReceiveResult terminal;
            do
            {
                terminal = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), bound.Token);
                if (terminal.MessageType == WebSocketMessageType.Text)
                {
                    using var frame = JsonDocument.Parse(buffer.AsMemory(0, terminal.Count));
                    frame.RootElement.TryGetProperty("result", out _).Should().BeFalse();
                }
            } while (terminal.MessageType != WebSocketMessageType.Close);
            terminal.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
            terminal.CloseStatusDescription.Should().Be("authorization-ended");
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token));
            request.Headers.Referrer = new Uri(referer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            (await ReadUntilAsync(reader, "event: status", ct)).Should().Contain("connected");
            var expectedId = await PublishAsync(73.25);
            var pushed = await ReadUntilAsync(reader, "event: observation", ct);
            using var observation = JsonDocument.Parse(pushed[(pushed.IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
            observation.RootElement.GetProperty("result").GetDouble().Should().Be(73.25);
            observation.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(expectedId);
            await InvalidateAsync();
            SetBound();
            var remaining = await reader.ReadToEndAsync(bound.Token);
            remaining.Should().Contain("\"code\":\"authorization-ended\"").And.NotContain("event: observation");
        }

        using var rejected = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token));
        rejected.Headers.Referrer = new Uri(referer);
        rejected.Headers.Accept.ParseAdd("text/event-stream");
        using var denied = await _fixture.Client.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // STA has a live-only contract: replacement establishes a new subscription,
        // then observes a newly committed value rather than claiming replay support.
        var replacement = await IssueAsync(longLived: true);
        using var resumed = await ConnectAsync(replacement.Token);
        using var handshake = await ReadSocketFrameAsync(resumed, ct);
        handshake.RootElement.GetProperty("status").GetString().Should().Be("connected");
        var replacementId = await PublishAsync(91.75);
        using var replacementObservation = await ReadObservationAsync(resumed);
        replacementObservation.RootElement.GetProperty("result").GetDouble().Should().Be(91.75);
        replacementObservation.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(replacementId);
    }
}
