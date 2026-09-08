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

/// <summary>Concurrent delivery proofs using distinct, genuinely authenticated subscribers.</summary>
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
                new() { Role = "*", Service = "*", Layer = "*", Attribute = "NAME", ClaimType = ClaimTypes.Role },
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

    /// <summary>Tenant policy must filter even an administrator's unfiltered live stream.</summary>
    [IntegrationTheory]
    [Operation(Operations.Streaming)]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Stream_TwoConcurrentTenants_ForeignEventIsAbsentBeforeOwnSentinel(bool webSocket)
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = timeout.Token;
        var issuer = fixture.GetService<IPortalTokenIssuer>();
        var alice = await IssueSplitTokenAsync(issuer, "alice", "reader", ct, "tenant-a");
        var bob = await IssueSplitTokenAsync(issuer, "bob", "admin", ct, "tenant-b");
        var anchor = await fixture.GetService<IFeatureChangeEventStore>().AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "test", LayerId = 0, ObjectId = 89000, Operation = "update",
            Protocol = "rest", RequestId = "tenant-split-anchor"
        }, ct);

        await using var aliceStream = await OpenSplitStreamAsync(fixture, webSocket,
            $"/api/v1/streaming/features?serviceId=test&layers=0&cursor={anchor.Cursor}&token={alice}", ct);
        // No service, layer or row filter can conceal a broken tenant delivery policy here.
        await using var bobStream = await OpenSplitStreamAsync(fixture, webSocket,
            $"/api/v1/streaming/features?cursor={anchor.Cursor}&token={bob}", ct);
        var publisher = fixture.GetService<IFeatureChangeEventPublisher>();
        async Task PublishAsync(int layerId, long objectId, string marker) =>
            await publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test", LayerId = layerId, ObjectId = objectId, Operation = "update",
                Protocol = "rest", RequestId = marker, PropertiesJson = JsonSerializer.Serialize(new { name = marker })
            }, ct);

        await PublishAsync(1, 89201, "tenant-b-before");
        await PublishAsync(0, 89101, "tenant-a-private-value");
        var aliceSeen = await ReadFeatureChangesUntilAsync(aliceStream, 89101, ct);
        aliceSeen.Select(frame => frame.ObjectId).Should().Equal(89101);
        aliceSeen.Single().Raw.Should().Contain("tenant-a-private-value").And.NotContain("tenant-b-before");

        // A later permitted event is an ordered positive control: Bob has processed the
        // foreign event before this sentinel, so absence is not inferred from a timeout.
        await PublishAsync(1, 89202, "tenant-b-after");
        var bobSeen = await ReadFeatureChangesUntilAsync(bobStream, 89202, ct);
        bobSeen.Select(frame => frame.ObjectId).Should().Equal(89201, 89202);
        bobSeen.Should().NotContain(frame => frame.ObjectId == 89101);
        bobSeen.Should().OnlyContain(frame => !frame.Raw.Contains("tenant-a-private-value"));
        bobSeen[0].Raw.Should().Contain("tenant-b-before");
        bobSeen[1].Raw.Should().Contain("tenant-b-after");
    }

    /// <summary>The WebSocket counterpart of the SSE unauthenticated-connect rejection.</summary>
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

        // The TestHost client reports the refused upgrade as
        // "Incomplete handshake, status code: {status}". Assert on the status it names rather
        // than with a regular expression: FluentAssertions applies a match timeout, and under a
        // loaded host that timeout — not the assertion — is what fails.
        exception.Message.Should().ContainAny(
            ["401", "403"],
            "an unauthenticated WebSocket subscriber must be refused the upgrade, not handed a "
            + "socket; the client reported: {0}",
            exception.Message);
        exception.Message.Should().NotContain("101", "no protocol upgrade may be completed");
    }

    private static async Task<string> IssueSplitTokenAsync(
        IPortalTokenIssuer issuer,
        string principalId,
        string role,
        CancellationToken cancellationToken,
        string? tenantId = null)
        => (await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                principalId,
                principalId,
                TenantId: tenantId,
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
        private readonly HttpClient? _client;
        private readonly HttpResponseMessage? _response;
        private readonly Stream? _stream;
        private readonly StreamReader? _reader;

        private SplitStream(WebSocket socket) => _socket = socket;

        private SplitStream(HttpClient client, HttpResponseMessage response, Stream stream, StreamReader reader)
        {
            _client = client;
            _response = response;
            _stream = stream;
            _reader = reader;
        }

        public static SplitStream ForSocket(WebSocket socket) => new(socket);

        public static SplitStream ForSse(HttpClient client, HttpResponseMessage response, Stream stream, StreamReader reader)
            => new(client, response, stream, reader);

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
            _client?.Dispose();
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
            var socketClient = fixture.CreateWebSocketClient();
            var configure = socketClient.ConfigureRequest;
            socketClient.ConfigureRequest = request =>
            {
                configure?.Invoke(request);
                request.Headers.Referer = SplitReferer;
            };
            var socket = await socketClient.ConnectAsync(new Uri("ws://localhost" + path), cancellationToken);
            var stream = SplitStream.ForSocket(socket);

            // Drain the connected/handshake frame so the first read below is a delivery.
            _ = await stream.NextAsync(cancellationToken);
            return stream;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Referrer = new Uri(SplitReferer);
        request.Headers.Accept.ParseAdd("text/event-stream");

        // A dedicated client per subscriber: the fixture's shared client can carry an ambient
        // admin credential, and each subscriber must be authenticated only by its own token.
        var client = fixture.CreateClient();
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Read the body only when the admission failed. An SSE body never ends, so evaluating it
        // as an assertion argument would block until the test's timeout fired.
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            var failure = await response.Content.ReadAsStringAsync(cancellationToken);
            response.StatusCode.Should().Be(
                System.Net.HttpStatusCode.OK,
                "both subscribers must be admitted; only what they RECEIVE may differ: {0}",
                failure);
        }

        var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new StreamReader(body);
        var sse = SplitStream.ForSse(client, response, body, reader);
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
