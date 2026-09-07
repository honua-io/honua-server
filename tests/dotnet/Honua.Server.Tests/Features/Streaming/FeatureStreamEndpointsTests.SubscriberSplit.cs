// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;

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
/// The split below is deliberately built on the <b>row</b> policy rather than on layer access or
/// tenant scope. Both of those are enforced at <i>admission</i>: a subscriber that may not read a
/// layer — including one whose credential carries the wrong tenant — is refused the connection
/// outright by <c>RequireStreamLayerAccess</c>, which is the property
/// <c>Stream_RealPortalCredentialExpiresOrIsRevoked_TerminatesAndReplacementResumes</c> already
/// proves. To make the split observable <i>on delivery</i> both subscribers must be admitted on
/// the same layer under the same scope, differing only in identity — which is exactly what a
/// role-derived row predicate does.
/// </para>
/// </summary>
public sealed partial class FeatureStreamEndpointsTests
{
    private const string SplitReferer = "https://subscriber-split-proof.example/";

    /// <summary>Role held by the first subscriber, and the row value it entitles.</summary>
    private const string AlphaRole = "alpha-reader";

    /// <summary>Role held by the second subscriber, and the row value it entitles.</summary>
    private const string BetaRole = "beta-reader";

    /// <summary>
    /// Two concurrent subscribers, one layer, one scope, two identities: each receives only the
    /// rows its own credential entitles it to, and the other subscriber's rows never appear.
    /// </summary>
    [IntegrationTheory]
    [Operation(Operations.Streaming)]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_TwoConcurrentSubscribersWithDifferentGrants_ReceiveDisjointStreams(bool webSocket)
    {
        // The real row-policy seam: the predicate is "the row's `name` must be one of the
        // caller's role claims", so alice and bob resolve to different predicates from the same
        // policy row. This is the store the production path consults; only its rows are supplied.
        var rows = Substitute.For<IRlsPolicyStore>();
        rows.GetEffectivePoliciesAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RlsPolicy[]
            {
                new() { Role = "*", Service = "*", Layer = "*", Attribute = "name", ClaimType = ClaimTypes.Role },
            });

        await using var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IRlsPolicyStore>(rows)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
        await fixture.InitializeAsync();

        // One layer, readable by both roles: admission must NOT be what separates them.
        fixture.UpdateV2ResourceMetadata(
            0,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [AlphaRole, BetaRole] });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = timeout.Token;

        var issuer = fixture.GetService<IPortalTokenIssuer>();
        var alice = await IssueSplitTokenAsync(issuer, "alice", AlphaRole, ct);
        var bob = await IssueSplitTokenAsync(issuer, "bob", BetaRole, ct);

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
        async Task PublishAsync(long objectId, string entitledRole, string marker)
            => await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 0,
                ObjectId = objectId,
                Operation = "update",
                Protocol = "rest",
                RequestId = $"split-{objectId}",
                PropertiesJson = $"{{\"name\":\"{entitledRole}\",\"marker\":\"{marker}\"}}",
            }, ct);

        // Identical URLs but for the credential.
        var path = $"/api/v1/streaming/features?serviceId=test&layers=0&cursor={anchor.Cursor}&token=";

        await using var aliceStream = await OpenSplitStreamAsync(fixture, webSocket, path + alice, ct);
        await using var bobStream = await OpenSplitStreamAsync(fixture, webSocket, path + bob, ct);

        // Round 1: Bob's row first, then Alice's. Delivery is ordered, so Alice reading up to
        // hers proves Bob's row was never going to be delivered to her.
        await PublishAsync(88201, BetaRole, "beta-only-secret-1");
        await PublishAsync(88101, AlphaRole, "alpha-only-secret-1");
        var aliceSeen = await ReadFeatureChangesUntilAsync(aliceStream, 88101, ct);

        // Round 2: the mirror image for Bob.
        await PublishAsync(88102, AlphaRole, "alpha-only-secret-2");
        await PublishAsync(88202, BetaRole, "beta-only-secret-2");
        var bobSeen = await ReadFeatureChangesUntilAsync(bobStream, 88202, ct);

        // Alice must also have advanced past round 2's alpha row without ever seeing beta.
        aliceSeen.AddRange(await ReadFeatureChangesUntilAsync(aliceStream, 88102, ct));

        aliceSeen.Select(frame => frame.ObjectId).Should().OnlyContain(
            objectId => objectId == 88101 || objectId == 88102,
            "the alpha-entitled subscriber must receive only the rows its credential entitles it to");
        bobSeen.Select(frame => frame.ObjectId).Should().OnlyContain(
            objectId => objectId == 88201 || objectId == 88202,
            "the beta-entitled subscriber must receive only the rows its credential entitles it to");

        foreach (var frame in aliceSeen)
        {
            frame.Raw.Should().NotContain("beta-only-secret");
            frame.Raw.Should().NotContain(BetaRole);
        }

        foreach (var frame in bobSeen)
        {
            frame.Raw.Should().NotContain("alpha-only-secret");
            frame.Raw.Should().NotContain(AlphaRole);
        }

        // Positive controls on both sides: neither stream was simply silent.
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
        CancellationToken cancellationToken)
        => (await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                principalId,
                principalId,
                TenantId: null,
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
