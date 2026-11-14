// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Security.Claims;
using Honua.Alerts;
using Honua.Server.Features.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Realtime;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Honua.Geoprocessing;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class RealtimeNotificationEndpointsTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("jobs:0", true)]
    [InlineData("jobs:9223372036854775807", true)]
    [InlineData("jobs:-1", false)]
    [InlineData("jobs:9223372036854775808", false)]
    [InlineData("catalog:1", false)]
    [InlineData("1", false)]
    public void Cursor_RejectsWrongFamilyAndInvalidSequences(string cursor, bool valid)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Last-Event-ID"] = cursor;
        Assert.Equal(valid, RealtimeNotificationEndpoints.TryParseCursor(context, RealtimeFamily.Jobs, out _));
    }

    [UnitTest]
    public async Task Jobs_Replay_IsScopedAndReauthorizesBeforeEmittingSafePayload()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var context = CreateContext("?jobId=mine");
        context.Request.Headers["Last-Event-ID"] = "jobs:3";
        jobs.GetJobAsync("mine", context.User, Arg.Any<CancellationToken>()).Returns(new ExecutionJobRecord { OperationId = "mine", Status = ExecutionJobStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec { TargetKind = default, Backend = "test", Kind = default, WorkloadName = "test" } });
        RealtimeSubscriptionRequest? request = null;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value =>
        {
            request = value;
            value.Writer.TryWrite(new RealtimeDelivery.Event(Event(4, "other")));
            value.Writer.TryWrite(new RealtimeDelivery.Event(Event(5, "mine")));
            value.Writer.TryComplete();
        })).Returns(Substitute.For<IRealtimeSubscription>());

        await RealtimeNotificationEndpoints.HandleJobsAsync(context, provider, jobs);

        Assert.Equal(3, request!.After);
        Assert.Equal(new[] { "mine" }, request.ScopeKeys);
        await jobs.Received(2).GetJobAsync("mine", context.User, Arg.Any<CancellationToken>());
        var output = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("id: jobs:5\nevent: job.status", output);
        Assert.Contains("\"jobId\":\"mine\"", output);
        Assert.DoesNotContain("other", output);
        Assert.DoesNotContain("requesterId", output);
        Assert.DoesNotContain("private-result", output);
    }

    [UnitTest]
    public async Task Jobs_RevokedOwnership_ClosesBeforeSendingQueuedEvent()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var context = CreateContext("?jobId=mine");
        provider.GetWindowAsync(RealtimeFamily.Jobs, Arg.Any<CancellationToken>())
            .Returns(new RealtimeLogWindow(RealtimeFamily.Jobs, 0, 0));
        var calls = 0;
        jobs.GetJobAsync("mine", context.User, Arg.Any<CancellationToken>()).Returns(_ =>
            ++calls == 1 ? Task.FromResult(new ExecutionJobRecord { OperationId = "mine", Status = ExecutionJobStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                Spec = new ExecutionJobSpec { TargetKind = default, Backend = "test", Kind = default, WorkloadName = "test" } }) :
                Task.FromException<ExecutionJobRecord>(new GeoprocessingAuthorizationException(false)));
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value =>
        {
            value.Writer.TryWrite(new RealtimeDelivery.Event(Event(1, "mine")));
        })).Returns(Substitute.For<IRealtimeSubscription>());

        await RealtimeNotificationEndpoints.HandleJobsAsync(context, provider, jobs);

        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [UnitTest]
    public async Task Jobs_TooManyScopes_RejectsBeforeAuthorizationOrSubscription()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var context = CreateContext("?" + string.Join("&", Enumerable.Range(0, 51).Select(id => $"jobId={id}")));

        var result = await RealtimeNotificationEndpoints.HandleJobsAsync(context, provider, jobs);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        provider.DidNotReceive().Subscribe(Arg.Any<RealtimeSubscriptionRequest>());
        await jobs.DidNotReceiveWithAnyArgs().GetJobAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task Layers_Replay_FiltersPrivateLayersAndCommitsCursorAfterLastVisibleLayer()
    {
        var before = LayerSnapshot("before", false);
        var after = LayerSnapshot("after", true);
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var graphs = Substitute.For<IMetadataV2GraphProvider>();
        var history = Substitute.For<IMetadataV2EnvironmentSnapshotReader>();
        graphs.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(after);
        history.GetByEtagAsync(after.Graph.Environment, "before", Arg.Any<CancellationToken>()).Returns(before);
        history.GetByEtagAsync(after.Graph.Environment, "after", Arg.Any<CancellationToken>()).Returns(after);
        var context = CreateContext("?serviceId=city");
        using var services = new ServiceCollection().AddOptions()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        context.RequestServices = services;
        context.Request.Headers["Last-Event-ID"] = "catalog:3";
        RealtimeSubscriptionRequest? request = null;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value =>
        {
            request = value;
            value.Writer.TryWrite(new RealtimeDelivery.Event(CatalogEvent(4, after.Graph.Environment)));
            value.Writer.TryComplete();
        })).Returns(Substitute.For<IRealtimeSubscription>());

        await RealtimeNotificationEndpoints.HandleLayersAsync(context, provider, graphs, history);

        Assert.Equal(3, request!.After);
        Assert.Equal(new[] { after.Graph.Environment }, request.ScopeKeys);
        var output = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("\"layerId\":0", output);
        Assert.Contains("\"layerId\":1", output);
        Assert.DoesNotContain("\"layerId\":2", output);
        Assert.DoesNotContain("private", output);
        Assert.Equal(1, output.Split("id: catalog:4").Length - 1);
        Assert.True(output.IndexOf("\"layerId\":0", StringComparison.Ordinal) <
            output.IndexOf("id: catalog:4", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task Layers_MissingHistory_ResetsInsteadOfGuessingChanges()
    {
        var current = LayerSnapshot("after", true);
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        provider.GetWindowAsync(RealtimeFamily.Catalog, Arg.Any<CancellationToken>())
            .Returns(new RealtimeLogWindow(RealtimeFamily.Catalog, 0, 0));
        var graphs = Substitute.For<IMetadataV2GraphProvider>();
        var history = Substitute.For<IMetadataV2EnvironmentSnapshotReader>();
        graphs.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(current);
        var context = CreateContext("?serviceId=city");
        using var services = new ServiceCollection().AddOptions()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        context.RequestServices = services;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value =>
        {
            value.Writer.TryWrite(new RealtimeDelivery.Event(CatalogEvent(4, current.Graph.Environment)));
            value.Writer.TryComplete();
        })).Returns(Substitute.For<IRealtimeSubscription>());

        await RealtimeNotificationEndpoints.HandleLayersAsync(context, provider, graphs, history);

        var output = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("event: reset", output);
        Assert.Contains("\"reason\":\"retention\"", output);
        Assert.DoesNotContain("layer.changed", output);
    }

    [UnitTest]
    public async Task Layers_WithoutReadableLayer_RejectsBeforeSubscribing()
    {
        var current = LayerSnapshot("after", true);
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var graphs = Substitute.For<IMetadataV2GraphProvider>();
        graphs.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(current);
        var context = CreateContext("?serviceId=city");
        var evaluator = Substitute.For<IAccessPolicyEvaluator>();
        evaluator.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<AccessPolicy?>(), Arg.Any<AccessPolicy?>(), Arg.Any<AccessScope>())
            .Returns(AccessDecision.Forbidden("revoked"));
        using var services = new ServiceCollection().AddOptions().AddSingleton(evaluator).BuildServiceProvider();
        context.RequestServices = services;

        var result = await RealtimeNotificationEndpoints.HandleLayersAsync(context, provider, graphs,
            Substitute.For<IMetadataV2EnvironmentSnapshotReader>());

        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeHttpResult)result).StatusCode);
        provider.DidNotReceive().Subscribe(Arg.Any<RealtimeSubscriptionRequest>());
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("jobs")]
    [InlineData("catalog")]
    public async Task Stream_WithoutCursor_CapturesHeadBeforeDelayedWorkerStarts(string familyName)
    {
        var family = new RealtimeFamily(familyName);
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var context = CreateContext("");
        provider.GetWindowAsync(family, Arg.Any<CancellationToken>())
            .Returns(new RealtimeLogWindow(family, 1, 7));
        var delivered = new List<long>();
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(request =>
        {
            // The worker starts after event 8 committed. A null cursor would resolve
            // to that newer head and skip the event published during registration.
            if ((request.After ?? 8) < 8)
            {
                request.Writer.TryWrite(new RealtimeDelivery.Event(Event(8, "mine") with { Family = family }));
            }
            request.Writer.TryComplete();
        })).Returns(Substitute.For<IRealtimeSubscription>());

        await RealtimeNotificationEndpoints.StreamAsync(context, provider,
            new RealtimeNotificationEndpoints.StreamRequest(family, [], null, (item, _) =>
            {
                delivered.Add(item.Sequence);
                return Task.FromResult(true);
            }));

        Assert.Collection(delivered, sequence => Assert.Equal(8, sequence));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Alerts_RetentionReset_SendsRecoveryFrameBeforeClosingWebSocket(bool acknowledgeClose)
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        provider.GetWindowAsync(RealtimeFamily.Alerts, Arg.Any<CancellationToken>())
            .Returns(new RealtimeLogWindow(RealtimeFamily.Alerts, 20, 42));
        var subscribed = new TaskCompletionSource<RealtimeSubscriptionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(request => subscribed.SetResult(request)))
            .Returns(Substitute.For<IRealtimeSubscription>());
        await using var broadcaster = new RealtimeAlertNotificationBroadcaster(
            provider, NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseWebSockets();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/alerts", (Delegate)(async (HttpContext context) =>
        {
            var result = await StreamingOperationsEndpoints.HandleAlertStream(broadcaster, context);
            finished.TrySetResult();
            return result;
        }));
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/alerts?cursor=alerts:1"), timeout.Token);
        var request = await subscribed.Task.WaitAsync(timeout.Token);
        // Delivery is delayed until the endpoint is connected and accepting frames.
        await socket.SendAsync(new ArraySegment<byte>(new byte[] { 1 }), WebSocketMessageType.Binary, true, timeout.Token);
        request.Writer.TryWrite(new RealtimeDelivery.Reset(RealtimeResetReasons.Retention, 42));
        request.Writer.TryComplete();
        var buffer = new byte[1024];

        var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        if (frame.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", timeout.Token);
        }
        Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        using var payload = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
        Assert.Equal("reset", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("retention", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal(42, payload.RootElement.GetProperty("head").GetInt64());
        var close = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        if (acknowledgeClose)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", timeout.Token);
        }
        await finished.Task.WaitAsync(timeout.Token);
    }

    private static MetadataV2GraphSnapshot LayerSnapshot(string etag, bool changed)
    {
        var builder = new TestMetadataV2GraphBuilder().AddService("svc-city", "city", protocols: ServiceProtocols.All);
        for (var id = 0; id < 3; id++)
        {
            builder.AddResource($"res-{id}", changed ? $"changed-{id}" : $"original-{id}",
                    accessPolicy: id == 2 ? new AccessPolicy { AllowedRoles = ["private"] } : null)
                .AddPublication($"pub-{id}", "svc-city", $"res-{id}", layerIndex: id);
        }
        return new MetadataV2GraphSnapshot(builder.Build(), etag, DateTimeOffset.UtcNow);
    }

    private static RealtimeEvent CatalogEvent(long sequence, string environment) => new(
        RealtimeFamily.Catalog, sequence, environment, "catalog.changed",
        JsonElement.Parse("{\"etag\":\"after\",\"previousEtag\":\"before\"}"), DateTimeOffset.UtcNow);

    private static DefaultHttpContext CreateContext(string query)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "requester")], "test"))
        };
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static RealtimeEvent Event(long sequence, string jobId) => new(
        RealtimeFamily.Jobs, sequence, jobId, "job.status", JsonElement.Parse(
            "{\"status\":\"Running\",\"updatedAt\":\"2026-11-14T00:00:00Z\",\"requesterId\":\"secret\",\"result\":\"private-result\"}"),
        DateTimeOffset.UtcNow);
}
