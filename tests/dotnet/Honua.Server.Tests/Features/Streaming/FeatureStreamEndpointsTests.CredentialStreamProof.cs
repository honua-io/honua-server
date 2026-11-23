// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    // One credential-bound feature stream for the authorization cases: publishes paired tenant-a and
    // tenant-b changes and subscribes to layer 0 from the anchor cursor over WebSocket or SSE.
    private sealed class CredentialStreamProof(WebAppFixture fixture, long anchorCursor, CancellationToken ct)
    {
        internal const string Referer = "https://stream-proof.example/";
        internal const long MarkedObjectId = 73001;

        private readonly IPortalTokenIssuer _issuer = fixture.GetService<IPortalTokenIssuer>();
        private readonly List<SetupFrame> _setupFrames = [];

        public long LastCursor { get; private set; }

        public StreamReader? SseReader { get; private set; }

        public async Task<ExpiringCredential> IssueAsync(TimeSpan ttl, string? tenant = "tenant-a") =>
            ExpiringCredential.From(await _issuer.IssueAsync(
                new PortalTokenIssueRequest("stream-proof", "Stream proof", tenant, ["reader"],
                    PortalTokenClientType.Referer, Referer, DateTimeOffset.UtcNow + ttl), ct));

        public Task RevokeAsync(ExpiringCredential credential) => _issuer.RevokeAsync(credential.Token, ct);

        public async Task<long> PublishAsync(long id)
        {
            await fixture.AppendChangeAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 1,
                ObjectId = id + 100,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"tenant-b-{id}",
                PropertiesJson = "{\"name\":\"tenant-b-secret\"}"
            }, ct);
            var published = await fixture.AppendChangeAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 0,
                ObjectId = id,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"auth-proof-{id}",
                PropertiesJson = "{\"name\":\"credential-proof\"}"
            }, ct);
            return published.Cursor;
        }

        public async Task<WebSocket> ConnectAsync(string token, long? cursor = null)
        {
            var client = fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request =>
            {
                configure?.Invoke(request);
                request.Headers["X-Honua-Test-Schema"] = fixture.CurrentSchema;
                request.Headers.Referer = Referer;
            };
            return await client.ConnectAsync(new Uri("ws://localhost" + Path(token, cursor)), ct);
        }

        public static HttpRequestMessage SseRequest(string token, long? cursor)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Path(token, cursor));
            request.Headers.Referrer = new Uri(Referer);
            request.Headers.Accept.ParseAdd("text/event-stream");
            return request;
        }

        public async Task<WebSocket> SubscribeWebSocketAsync(ExpiringCredential issued)
        {
            var opened = await ConnectAsync(issued.Token, anchorCursor);
            try
            {
                CheckHandshake(await ReceiveWebSocketJsonAsync(opened, ct));
                await ReadToPublishedChangeAsync(async () =>
                {
                    var frame = await ReceiveWebSocketJsonAsync(opened, ct);
                    return (frame.GetProperty("type").GetString() == "feature-change", frame);
                });
                return opened;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }

        public async Task<HttpResponseMessage> SubscribeSseAsync(ExpiringCredential issued)
        {
            using var request = SseRequest(issued.Token, anchorCursor);
            var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                SseReader = reader;
                CheckHandshake((await ReadNextSseEventAsync(reader, ct)).Data);
                await ReadToPublishedChangeAsync(async () =>
                {
                    var sse = await ReadNextSseEventAsync(reader, ct);
                    return (sse.EventName == "feature-change", sse.Data);
                });
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        // A setup attempt the credential outlived is discarded, so the frames of every attempt are checked again.
        public void AssertEverySetupFrameWasClean()
        {
            foreach (var frame in _setupFrames)
            {
                CheckFrame(frame);
            }
        }

        private static string Path(string token, long? cursor) =>
            $"/api/v1/streaming/features?serviceId=test&layers=0&token={token}"
            + (cursor.HasValue ? $"&cursor={FeatureStreamCursor.Format(cursor.Value)}" : "");

        // A retried setup also receives the earlier attempt's change, so it reads on to the change it published.
        private async Task ReadToPublishedChangeAsync(Func<Task<(bool IsChange, JsonElement Data)>> next)
        {
            var published = await PublishAsync(MarkedObjectId);
            (bool IsChange, JsonElement Data) frame;
            do
            {
                frame = await next();
                CheckFrame(Record(new SetupFrame(frame.Data.GetRawText(), frame.IsChange ? FrameKind.Change : FrameKind.Other)));
            }
            while (!frame.IsChange || frame.Data.GetProperty("cursor").GetInt64() != published);
            LastCursor = published;
        }

        private void CheckHandshake(JsonElement frame) =>
            CheckFrame(Record(new SetupFrame(frame.GetRawText(), FrameKind.Handshake)));

        private SetupFrame Record(SetupFrame frame)
        {
            _setupFrames.Add(frame);
            return frame;
        }

        private static void CheckFrame(SetupFrame frame)
        {
            frame.Raw.Should().NotContain(frame.Kind == FrameKind.Handshake ? "tenant-b" : "tenant-b-secret");
            if (frame.Kind == FrameKind.Change)
            {
                using var change = JsonDocument.Parse(frame.Raw);
                change.RootElement.GetProperty("objectId").GetInt64().Should().Be(MarkedObjectId);
            }
        }

        private enum FrameKind
        {
            Handshake,
            Other,
            Change,
        }

        private sealed record SetupFrame(string Raw, FrameKind Kind);
    }
}
