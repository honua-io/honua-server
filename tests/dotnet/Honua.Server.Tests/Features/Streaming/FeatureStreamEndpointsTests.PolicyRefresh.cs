// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Open subscriptions re-resolve access, row-level security and field masks on the
/// subscriber-policy refresh tick (honua-server#6105).
/// </summary>
public sealed partial class FeatureStreamEndpointsTests
{
    private static readonly TimeSpan PolicyRefreshInterval = TimeSpan.FromMilliseconds(250);

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public Task Sse_OpenSubscription_FieldMaskAddedAfterConnect_AppliesToLaterEvents()
        => VerifyFieldMaskAddedAfterConnectAsync(webSocket: false);

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public Task WebSocket_OpenSubscription_FieldMaskAddedAfterConnect_AppliesToLaterEvents()
        => VerifyFieldMaskAddedAfterConnectAsync(webSocket: true);

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_OpenSubscription_LayerAccessRevoked_EndsStreamWithinRefreshInterval()
    {
        var masks = Substitute.For<IFieldMaskPolicyStore>();
        masks.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldMaskPolicy>());
        var fixture = CreatePolicyRefreshFixture(masks);
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = fixture.CreateAdminClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&clientLabel=policy-revoke");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            _ = await ReadNextSseEventAsync(reader, cts.Token);

            // The admin principal does not hold this role, so REST reads of layer 0 are now refused.
            fixture.UpdateV2ResourceMetadata(0, accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["revoked-stream-reader"] });

            using var ended = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            ended.CancelAfter(TimeSpan.FromSeconds(10));
            var closed = false;
            try
            {
                while (await reader.ReadLineAsync(ended.Token) is { } line)
                {
                    line.Should().NotContain("feature-change", "no frame may be delivered after access is revoked");
                }

                closed = true;
            }
            catch (OperationCanceledException) when (ended.IsCancellationRequested)
            {
                // Stream stayed open past the deadline.
            }

            closed.Should().BeTrue("a subscription whose layer access was revoked must end within the refresh interval");
            fixture.GetService<FeatureStreamSessionManager>().GetSessions()
                .Should().NotContain(session => session.ClientLabel == "policy-revoke");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task VerifyFieldMaskAddedAfterConnectAsync(bool webSocket)
    {
        var masks = Substitute.For<IFieldMaskPolicyStore>();
        masks.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldMaskPolicy>());
        var fixture = CreatePolicyRefreshFixture(masks);
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var publisher = fixture.GetService<IFeatureChangeEventPublisher>();
            long nextObjectId = 500;
            Task PublishAsync() => publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 0,
                ObjectId = nextObjectId++,
                Operation = "update",
                Protocol = "rest",
                RequestId = "policy-refresh",
                PropertiesJson = """{"name":"visible","description":"secret-value"}""",
                ChangedAttributes = new Dictionary<string, object?> { ["name"] = "visible", ["description"] = "secret-delta" }
            }, cts.Token);

            const string path = "/api/v1/streaming/features?layers=0&clientLabel=policy-refresh";
            using var socket = webSocket
                ? await fixture.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost" + path), cts.Token)
                : null;
            if (socket is not null)
            {
                _ = await ReceiveWebSocketJsonAsync(socket, cts.Token);
            }

            using var client = webSocket ? null : fixture.CreateAdminClient();

            async Task<HttpResponseMessage?> OpenSseAsync(HttpClient? http)
            {
                if (http is null)
                {
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Accept.ParseAdd("text/event-stream");
                var opened = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                opened.StatusCode.Should().Be(HttpStatusCode.OK);
                return opened;
            }

            using var response = await OpenSseAsync(client);
            using var reader = response is null
                ? null
                : new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            if (reader is not null)
            {
                _ = await ReadNextSseEventAsync(reader, cts.Token);
            }

            {
                async Task<JsonElement> NextFeatureChangeAsync()
                {
                    while (true)
                    {
                        if (socket is not null)
                        {
                            var frame = await ReceiveWebSocketJsonAsync(socket, cts.Token);
                            if (frame.GetProperty("type").GetString() == "feature-change")
                            {
                                return frame;
                            }
                        }
                        else
                        {
                            var frame = await ReadNextSseEventAsync(reader!, cts.Token);
                            if (frame.EventName == "feature-change")
                            {
                                return frame.Data;
                            }
                        }
                    }
                }

                await PublishAsync();
                var unmasked = await NextFeatureChangeAsync();
                unmasked.GetProperty("attributes").TryGetProperty("description", out _)
                    .Should().BeTrue("no mask applies at connect time");

                masks.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new FieldMaskPolicy[] { new() { Role = "*", Service = "*", Layer = "*", Attribute = "description" } });

                // Frames matched before the next refresh tick may still carry the field; once the
                // tick has passed, every later frame must be projected without it.
                await Task.Delay(PolicyRefreshInterval * 4, cts.Token);
                await PublishAsync();
                var masked = await NextFeatureChangeAsync();
                masked.GetProperty("attributes").EnumerateObject().Select(p => p.Name).Should().Equal("name");
                masked.GetProperty("changedAttributes").EnumerateObject().Select(p => p.Name).Should().Equal("name");
                masked.GetRawText().Should().NotContain("secret-");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreatePolicyRefreshFixture(IFieldMaskPolicyStore masks)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService(masks)
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:SubscriberPolicyRefreshInterval"] = PolicyRefreshInterval.ToString()
                })));
}
