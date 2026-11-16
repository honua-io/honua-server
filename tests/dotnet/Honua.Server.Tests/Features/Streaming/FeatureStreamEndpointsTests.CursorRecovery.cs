// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_ExpiredDeltaCursor_SendsRetentionResetThenSnapshot()
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&cursor=features:1");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        SseEvent frame;
        do
        {
            frame = await ReadNextSseEventAsync(reader, timeout.Token);
        }
        while (frame.EventName == "status");
        frame.EventName.Should().Be("reset");
        frame.Data.GetProperty("reason").GetString().Should().Be("retention");
        frame.Data.GetProperty("head").GetString().Should().Be("features:60");
        (await ReadNextSseEventAsync(reader, timeout.Token)).EventName.Should().Be("snapshot-begin");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sse_ExpiredUnsafeSnapshotScope_ResetsWithoutPublishingSnapshotCheckpoint(bool filtered)
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?cursor=features:1" + (filtered ? "&layers=0&bbox=-180,-90,180,90" : ""));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        body.Should().Contain("event: reset").And.Contain("retention");
        body.Should().NotContain("event: snapshot").And.NotContain("id: features:");
        body.Should().Contain(filtered ? "cannot be combined" : "explicit layer");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSocket_ExpiredUnsafeSnapshotScope_ResetsWithoutPublishingSnapshotCheckpoint(bool filtered)
    {
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(FakeFeatureChangeEventStore.WithRetentionWindow(
                FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50)));
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socketClient = fixture.CreateWebSocketClient();
        var configureRequest = socketClient.ConfigureRequest;
        socketClient.ConfigureRequest = request =>
        {
            configureRequest?.Invoke(request);
            request.Headers["X-API-Key"] = WebAppFixture.SharedAdminPassword;
        };
        using var socket = await socketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?cursor=features:1" + (filtered ? "&layers=0&bbox=-180,-90,180,90" : "")), timeout.Token);
        var status = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        status.GetProperty("type").GetString().Should().Be("status");
        if (filtered)
        {
            var subscribed = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
            subscribed.GetProperty("status").GetString().Should().Be("subscribed");
        }
        var reset = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        reset.GetProperty("type").GetString().Should().Be("reset");
        reset.GetProperty("reason").GetString().Should().Be("retention");
        var error = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        error.GetProperty("type").GetString().Should().Be("status");
        error.GetProperty("status").GetString().Should().Be("error");
        error.GetProperty("message").GetString().Should().Contain(filtered ? "cannot be combined" : "explicit layer");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task WebSocket_RetentionAdvancesDuringReplayOrLiveSweep_SendsTypedReset(bool duringReplay, bool nonemptyPage)
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
        if (nonemptyPage)
        {
            store.Append(new FeatureChangeEventRequest
            {
                ServiceId = "test",
                LayerId = 0,
                ObjectId = 9001,
                Operation = "update",
                Protocol = "rest",
                RequestId = "retention-websocket-nonempty-page"
            }, cursorGap: 40);
            store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
        }
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(store);
        await fixture.InitializeAsync();
        void AdvanceRetention()
        {
            store.QueryCompleted = null;
            store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 100, oldestRetainedCursor: 80));
        }

        if (duringReplay)
        {
            store.QueryCompleted = AdvanceRetention;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var query = duringReplay ? "?layers=0&cursor=features:59" : "?layers=0";
        using var socket = await fixture.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features" + query), timeout.Token);
        _ = await ReceiveWebSocketJsonAsync(socket, timeout.Token);
        if (!duringReplay)
        {
            AdvanceRetention();
        }

        var reset = await ReadWebSocketAfterStatusesAsync(socket, timeout.Token);
        reset.GetProperty("type").GetString().Should().Be("reset");
        reset.GetProperty("reason").GetString().Should().Be("retention");
        reset.GetProperty("head").GetString().Should().Be("features:100");
    }

    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sse_RetentionAdvancesDuringNonemptyReplay_ResetsBeforeDeliveringPage(bool filtered)
    {
        var store = FakeFeatureChangeEventStore.WithRetentionWindow(
            FeatureChangeRetentionWindow.Retained(currentCursor: 60, oldestRetainedCursor: 50));
        SeedRetentionReplayPage(store);
        store.QueryCompleted = () =>
        {
            store.QueryCompleted = null;
            store.SetRetentionWindow(FeatureChangeRetentionWindow.Retained(currentCursor: 100, oldestRetainedCursor: 80));
        };
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IFeatureChangeEventStore>(store);
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/streaming/features?layers=0&cursor=features:59" + (filtered ? "&bbox=-180,-90,180,90" : ""));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        SseEvent frame;
        do
        {
            frame = await ReadNextSseEventAsync(reader, timeout.Token);
        }
        while (frame.EventName == "status");
        frame.EventName.Should().Be("reset");
        frame.Data.GetProperty("reason").GetString().Should().Be("retention");
        if (filtered)
        {
            var error = await ReadNextSseEventAsync(reader, timeout.Token);
            error.EventName.Should().Be("status");
            error.Data.GetProperty("status").GetString().Should().Be("error");
            error.Data.GetProperty("message").GetString().Should().Contain("cannot be combined");
        }
    }
}
