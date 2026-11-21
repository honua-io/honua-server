// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed partial class SensorThingsStreamEndpointsTests
{
    private static readonly TimeSpan DurableCredentialLifetime = TimeSpan.FromSeconds(120);

    [IntegrationTheory]
    [Operation(Operations.Streaming)]
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
        using var proof = new ObservationStreamProof(_fixture, ct);
        ExpiringCredential credential;
        // Warm real ingestion/authentication paths before starting the credential's
        // clock; startup/JIT latency is not part of a connected expiry scenario.
        _ = await proof.PublishAsync(0.125);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void SetBound() => bound.CancelAfter(expire
            ? credential.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(5));
        async Task InvalidateAsync()
        {
            if (!expire) { await proof.RevokeAsync(credential); }
        }

        if (webSocket)
        {
            var started = await ExpiringCredentials.IssueForSetupAsync(
                expire ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable, proof.IssueAsync, proof.SubscribeWebSocketAsync);
            credential = started.Credential;
            using var socket = started.Result;
            proof.AssertEverySetupFrameWasClean();
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
                    frame.RootElement.TryGetProperty("observations", out _).Should().BeFalse();
                }
            } while (terminal.MessageType != WebSocketMessageType.Close);
            terminal.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
            terminal.CloseStatusDescription.Should().Be("authorization-ended");
        }
        else
        {
            var started = await ExpiringCredentials.IssueForSetupAsync(
                expire ? ExpiringCredentials.Expiring : ExpiringCredentials.Durable, proof.IssueAsync, proof.SubscribeSseAsync);
            credential = started.Credential;
            using var response = started.Result;
            using var reader = proof.SseReader!;
            proof.AssertEverySetupFrameWasClean();
            await InvalidateAsync();
            SetBound();
            var remaining = await reader.ReadToEndAsync(bound.Token);
            remaining.Should().Contain("\"code\":\"authorization-ended\"").And.NotContain("event: observations");
        }

        using var rejected = ObservationStreamProof.SseRequest(credential.Token);
        using var denied = await _fixture.Client.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Replacing credentials without a cursor starts a live subscription;
        // verify that it receives newly committed values under the replacement identity.
        var replacement = await proof.IssueAsync(DurableCredentialLifetime);
        using var resumed = await proof.ConnectAsync(replacement.Token);
        using var handshake = await ReadSocketFrameAsync(resumed, ct);
        handshake.RootElement.GetProperty("status").GetString().Should().Be("connected");
        var replacementId = await proof.PublishAsync(91.75);
        using var replacementObservation = await proof.ReadObservationAsync(resumed);
        replacementObservation.RootElement.GetProperty("observations")[0].GetProperty("result").GetDouble().Should().Be(91.75);
        replacementObservation.RootElement.GetProperty("observations")[0].GetProperty("id").GetInt64().Should().Be(replacementId);
    }
}
