// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_RealPortalCredentialExpiresOrIsRevoked_TerminatesAndReplacementResumes(bool webSocket, bool expire)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Tenant = "tenant-b" });
        fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        fixture.UpdateV2ResourceMetadata(1, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["reader"] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var issuer = fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://stream-proof.example/";
        async Task<PortalTokenIssuance> IssueAsync(TimeSpan ttl, string? tenant = "tenant-a") => await issuer.IssueAsync(
            new PortalTokenIssueRequest("stream-proof", "Stream proof", tenant, ["reader"],
                PortalTokenClientType.Referer, referer, DateTimeOffset.UtcNow + ttl), ct);
        var anchor = await fixture.GetService<IFeatureChangeEventStore>().AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = 73000, Operation = "update", Protocol = "rest", RequestId = "auth-anchor"
        }, ct);
        var credential = await IssueAsync(expire ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(1));
        var publisher = fixture.GetService<IFeatureChangeEventPublisher>();
        async Task PublishAsync(long id)
        {
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test", LayerId = 1, ObjectId = id + 100, Operation = "update", Protocol = "rest",
                RequestId = $"tenant-b-{id}", PropertiesJson = "{\"name\":\"tenant-b-secret\"}"
            }, ct);
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test", LayerId = 0, ObjectId = id, Operation = "update", Protocol = "rest",
                RequestId = $"auth-proof-{id}", PropertiesJson = "{\"name\":\"credential-proof\"}"
            }, ct);
        }

        string Path(string token, long? cursor = null) =>
            $"/api/v1/streaming/features?serviceId=test&layers=0&token={token}" + (cursor.HasValue ? $"&cursor={cursor}" : "");
        async Task<WebSocket> ConnectAsync(string token, long? cursor = null)
        {
            var client = fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request => { configure?.Invoke(request); request.Headers.Referer = referer; };
            return await client.ConnectAsync(new Uri("ws://localhost" + Path(token, cursor)), ct);
        }

        long lastCursor;
        if (webSocket)
        {
            using var socket = await ConnectAsync(credential.Token, anchor.Cursor);
            _ = await ReceiveWebSocketJsonAsync(socket, ct);
            await PublishAsync(73001);
            JsonElement frame;
            do { frame = await ReceiveWebSocketJsonAsync(socket, ct); frame.GetRawText().Should().NotContain("tenant-b-secret"); }
            while (frame.GetProperty("type").GetString() != "feature-change");
            frame.GetProperty("objectId").GetInt64().Should().Be(73001);
            lastCursor = frame.GetProperty("cursor").GetInt64();
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(expire ? credential.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(5));
            var buffer = new byte[8192];
            WebSocketReceiveResult terminal;
            do
            {
                terminal = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), bound.Token);
                if (terminal.MessageType == WebSocketMessageType.Text)
                {
                    Encoding.UTF8.GetString(buffer, 0, terminal.Count).Should().NotContain("feature-change");
                }
            } while (terminal.MessageType != WebSocketMessageType.Close);
            terminal.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
            terminal.CloseStatusDescription.Should().Be("authorization-ended");
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token, anchor.Cursor));
            request.Headers.Referrer = new Uri(referer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            _ = await ReadNextSseEventAsync(reader, ct);
            await PublishAsync(73001);
            SseEvent frame;
            do { frame = await ReadNextSseEventAsync(reader, ct); frame.Data.GetRawText().Should().NotContain("tenant-b-secret"); } while (frame.EventName != "feature-change");
            frame.Data.GetProperty("objectId").GetInt64().Should().Be(73001);
            lastCursor = frame.Data.GetProperty("cursor").GetInt64();
            if (!expire) { await issuer.RevokeAsync(credential.Token, ct); }
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(expire ? credential.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(5));
            do
            {
                frame = await ReadNextSseEventAsync(reader, bound.Token);
                frame.EventName.Should().NotBe("feature-change");
            } while (!frame.Data.TryGetProperty("code", out _));
            frame.Data.GetProperty("code").GetString().Should().Be("authorization-ended");
            (await reader.ReadLineAsync(bound.Token)).Should().BeNull("the authorization outcome is terminal");
        }

        await PublishAsync(73002);
        var replacement = await IssueAsync(TimeSpan.FromMinutes(1));
        using var resumed = await ConnectAsync(replacement.Token, lastCursor);
        JsonElement replay;
        do { replay = await ReceiveWebSocketJsonAsync(resumed, ct); }
        while (replay.GetProperty("type").GetString() != "feature-change");
        replay.GetProperty("objectId").GetInt64().Should().Be(73002);
        replay.GetProperty("cursor").GetInt64().Should().BeGreaterThan(lastCursor);

        using var rejected = new HttpRequestMessage(HttpMethod.Get, Path(credential.Token, lastCursor));
        rejected.Headers.Referrer = new Uri(referer);
        rejected.Headers.Accept.ParseAdd("text/event-stream");
        using var denied = await fixture.Client.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var tenant in new string?[] { "tenant-b", null })
        {
            var changed = await IssueAsync(TimeSpan.FromMinutes(1), tenant);
            using var incompatible = new HttpRequestMessage(HttpMethod.Get, Path(changed.Token, lastCursor));
            incompatible.Headers.Referrer = new Uri(referer);
            incompatible.Headers.Accept.ParseAdd("text/event-stream");
            using var hidden = await fixture.Client.SendAsync(incompatible, HttpCompletionOption.ResponseHeadersRead, ct);
            hidden.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a tenant-scoped layer cannot be resolved from another or unscoped tenant");
            (await hidden.Content.ReadAsStringAsync(ct)).Should().NotContain("credential-proof").And.NotContain("tenant-b-secret");
        }
    }
}
