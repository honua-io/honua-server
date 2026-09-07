// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// #4427: the delivery-level authorization split the realtime suite did not have.
/// <para>
/// Across sixteen streaming test files exactly three lines turned the F1 development-authentication
/// bypass off, and none of the three was a delivery test. Everywhere else the bypass returned an
/// <c>admin</c> principal before any header was read, so <c>IsAdmin(context.User)</c> was always
/// true and <c>StreamSubscriberSecurity</c> was only ever exercised with one connection and one
/// identity. The subscriber row/field policy tests are good, but their policies are NSubstitute
/// fakes returning <c>Role = "*"</c> and there is a single dev-bypass admin subscriber: they prove
/// the projection function runs on the wire, not that two differently-authorized subscribers get
/// different streams.
/// </para>
/// <para>
/// This test opens two <b>concurrent</b> subscriptions under two real, non-admin portal
/// credentials scoped to different layers and tenants, publishes into both layers, and asserts
/// each subscriber's stream is disjoint. The ordering makes "receives nothing" observable rather
/// than merely un-observed: the foreign event is always published <i>before</i> the subscriber's
/// own event, and stream delivery is ordered, so reading until the own event arrives proves the
/// foreign event was never going to be delivered.
/// </para>
/// </summary>
public sealed partial class FeatureStreamEndpointsTests
{
    private const string SplitReferer = "https://subscriber-split-proof.example/";

    [IntegrationTheory]
    [Operation(Operations.Streaming)]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_TwoConcurrentSubscribersWithDifferentGrants_ReceiveDisjointStreams(bool webSocket)
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();

        // Two protected layers in one service, on two tenants, each gated by a different role.
        fixture.MutateV2ResourceObjectMetadata(0, metadata => metadata with { Tenant = "tenant-a" });
        fixture.MutateV2ResourceObjectMetadata(1, metadata => metadata with { Tenant = "tenant-b" });
        fixture.UpdateV2ResourceMetadata(
            0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["alpha-reader"] });
        fixture.UpdateV2ResourceMetadata(
            1, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["beta-reader"] });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;

        var issuer = fixture.GetService<IPortalTokenIssuer>();
        var alice = await IssueSplitTokenAsync(issuer, "alice", "alpha-reader", "tenant-a", ct);
        var bob = await IssueSplitTokenAsync(issuer, "bob", "beta-reader", "tenant-b", ct);

        var anchor = await fixture.GetService<IFeatureChangeEventStore>().AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = 88000,
            Operation = "update",
            Protocol = "rest",
            RequestId = "split-anchor",
        }, ct);

        var publisher = fixture.GetService<IFeatureChangeEventPublisher>();
        async Task PublishAsync(int layerId, long objectId, string marker)
            => await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = layerId,
                ObjectId = objectId,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"split-{objectId}",
                PropertiesJson = $"{{\"name\":\"{marker}\"}}",
            }, ct);

        // Both subscribe to the WHOLE service, not to a layer they are known to hold: the scope
        // is identical, so any difference in what they receive is the authorization decision.
        var path = $"/api/v1/streaming/features?serviceId=test&cursor={anchor.Cursor}&token=";

        await using var aliceStream = await OpenSplitStreamAsync(fixture, webSocket, path + alice, ct);
        await using var bobStream = await OpenSplitStreamAsync(fixture, webSocket, path + bob, ct);

        // Round 1: Bob's event first, then Alice's. Alice reading up to hers proves Bob's was
        // never hers to see.
        await PublishAsync(1, 88201, "beta-only-secret-1");
        await PublishAsync(0, 88101, "alpha-only-secret-1");
        var aliceSeen = await ReadFeatureChangesUntilAsync(aliceStream, 88101, ct);

        // Round 2: the mirror image for Bob.
        await PublishAsync(0, 88102, "alpha-only-secret-2");
        await PublishAsync(1, 88202, "beta-only-secret-2");
        var bobSeen = await ReadFeatureChangesUntilAsync(bobStream, 88202, ct);

        // Alice must also have advanced past round 2's alpha event without ever seeing beta.
        aliceSeen.AddRange((await ReadFeatureChangesUntilAsync(aliceStream, 88102, ct)));

        aliceSeen.Select(frame => frame.ObjectId).Should().OnlyContain(
            objectId => objectId == 88101 || objectId == 88102,
            "the alpha-only subscriber must receive only its own layer's events");
        bobSeen.Select(frame => frame.ObjectId).Should().OnlyContain(
            objectId => objectId == 88201 || objectId == 88202,
            "the beta-only subscriber must receive only its own layer's events");

        foreach (var frame in aliceSeen)
        {
            frame.Raw.Should().NotContain("beta-only-secret");
            frame.Raw.Should().NotContain("88201");
            frame.Raw.Should().NotContain("88202");
        }

        foreach (var frame in bobSeen)
        {
            frame.Raw.Should().NotContain("alpha-only-secret");
            frame.Raw.Should().NotContain("88101");
            frame.Raw.Should().NotContain("88102");
        }

        // Positive control on both sides: each subscriber really did receive its own payload, so
        // the disjointness above is not two silent streams.
        aliceSeen.Should().Contain(frame => frame.Raw.Contains("alpha-only-secret-1"));
        aliceSeen.Should().Contain(frame => frame.Raw.Contains("alpha-only-secret-2"));
        bobSeen.Should().Contain(frame => frame.Raw.Contains("beta-only-secret-2"));
    }

    /// <summary>
    /// #4427: the WebSocket counterpart of the existing SSE unauthenticated-connect rejection.
    /// Only the SSE case existed; the two WebSocket <c>ThrowsAsync</c> sites are session-limit
    /// (503) assertions, not authentication ones.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_ConnectWithoutCredential_IsRejectedBeforeAnyFrame()
    {
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(
            0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["alpha-reader"] });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;

        var client = fixture.CreateWebSocketClient();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?serviceId=test&layers=0"),
            ct));

        exception.Message.Should().MatchRegex(
            "40[13]",
            "an unauthenticated WebSocket subscriber must be refused the upgrade, not handed a socket");
        exception.Message.Should().NotContain("101", "no protocol upgrade may be completed");
    }

    private static async Task<string> IssueSplitTokenAsync(
        IPortalTokenIssuer issuer,
        string principalId,
        string role,
        string tenant,
        CancellationToken cancellationToken)
        => (await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                principalId,
                principalId,
                tenant,
                [role],
                PortalTokenClientType.Referer,
                SplitReferer,
                DateTimeOffset.UtcNow.AddMinutes(10)),
            cancellationToken)).Token;

    /// <summary>A frame delivered to one subscriber, with the object id it carried.</summary>
    private readonly record struct SplitFrame(long ObjectId, string Raw);

    /// <summary>
    /// One subscriber's live connection, abstracted over the two advertised transports so the
    /// split assertions read identically for SSE and WebSocket.
    /// </summary>
    private sealed class SplitStream : IAsyncDisposable
    {
        private readonly WebSocket? _socket;
        private readonly HttpResponseMessage? _response;
        private readonly Stream? _stream;
        private readonly StreamReader? _reader;

        private SplitStream(WebSocket socket) => _socket = socket;

        private SplitStream(HttpResponseMessage response, Stream stream, StreamReader reader)
        {
            _response = response;
            _stream = stream;
            _reader = reader;
        }

        public static SplitStream ForSocket(WebSocket socket) => new(socket);

        public static SplitStream ForSse(HttpResponseMessage response, Stream stream, StreamReader reader)
            => new(response, stream, reader);

        /// <summary>Reads the next frame of any type from the underlying transport.</summary>
        public async Task<(string? EventName, JsonElement Data)> NextAsync(CancellationToken cancellationToken)
        {
            if (_socket is not null)
            {
                var frame = await ReceiveWebSocketJsonAsync(_socket, cancellationToken);
                return (frame.GetProperty("type").GetString(), frame);
            }

            var sse = await ReadNextSseEventAsync(_reader!, cancellationToken);
            return (sse.EventName, sse.Data);
        }

        public ValueTask DisposeAsync()
        {
            _socket?.Dispose();
            _reader?.Dispose();
            _stream?.Dispose();
            _response?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<SplitStream> OpenSplitStreamAsync(
        WebAppFixture fixture,
        bool webSocket,
        string path,
        CancellationToken cancellationToken)
    {
        if (webSocket)
        {
            var client = fixture.CreateWebSocketClient();
            var configure = client.ConfigureRequest;
            client.ConfigureRequest = request =>
            {
                configure?.Invoke(request);
                request.Headers.Referer = SplitReferer;
            };
            var socket = await client.ConnectAsync(new Uri("ws://localhost" + path), cancellationToken);
            var stream = SplitStream.ForSocket(socket);

            // Drain the connected/handshake frame so the first read below is a delivery.
            _ = await stream.NextAsync(cancellationToken);
            return stream;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Referrer = new Uri(SplitReferer);
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.StatusCode.Should().Be(
            System.Net.HttpStatusCode.OK,
            "both subscribers must be admitted; only what they RECEIVE may differ: {0}",
            await response.Content.ReadAsStringAsync(cancellationToken));

        var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new StreamReader(body);
        var sse = SplitStream.ForSse(response, body, reader);
        _ = await sse.NextAsync(cancellationToken);
        return sse;
    }

    /// <summary>
    /// Reads <c>feature-change</c> frames until the one carrying <paramref name="untilObjectId"/>
    /// arrives, returning every feature-change frame seen on the way.
    /// </summary>
    private static async Task<List<SplitFrame>> ReadFeatureChangesUntilAsync(
        SplitStream stream,
        long untilObjectId,
        CancellationToken cancellationToken)
    {
        var seen = new List<SplitFrame>();
        while (true)
        {
            var (eventName, data) = await stream.NextAsync(cancellationToken);
            if (eventName != "feature-change")
            {
                continue;
            }

            var objectId = data.GetProperty("objectId").GetInt64();
            seen.Add(new SplitFrame(objectId, data.GetRawText()));
            if (objectId == untilObjectId)
            {
                return seen;
            }
        }
    }
}
