// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Realtime;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit tests for the MCP sessions + streaming spine (honua-server#1954):
/// per-session notification channels, the JSON-RPC notification publisher,
/// job-progress threading from the canonical job runtime, and the SSE pump. These
/// run without Docker/PostGIS so the server-push contract is covered even when the
/// full integration harness is unavailable.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpStreamingTests
{
    private static readonly ClaimsPrincipal Principal =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")], "Test"));

    [UnitTest]
    public void Session_EnqueueAndRead_DeliversFramesInOrder()
    {
        var sessions = new McpSessionManager();
        var id = sessions.CreateSession();

        sessions.TryEnqueue(id, "frame-1").Should().BeTrue();
        sessions.TryEnqueue(id, "frame-2").Should().BeTrue();
        sessions.TryEnqueue("never-issued", "x").Should().BeFalse();

        sessions.TryGetReader(id, out var reader).Should().BeTrue();
        reader.TryRead(out var first).Should().BeTrue();
        reader.TryRead(out var second).Should().BeTrue();
        first.Should().Be("frame-1");
        second.Should().Be("frame-2");
    }

    [UnitTest]
    public void Session_AssociateJob_RoutesJobToOwningSession()
    {
        var sessions = new McpSessionManager();
        var id = sessions.CreateSession();
        sessions.AssociateJob(id, "job-7");

        sessions.TryGetJobSession("job-7", out var resolved).Should().BeTrue();
        resolved.Should().Be(id);
        sessions.TryGetJobSession("unknown-job", out _).Should().BeFalse();
    }

    [UnitTest]
    public void Session_Terminate_CompletesChannelAndCancelsLifetime()
    {
        var sessions = new McpSessionManager();
        var id = sessions.CreateSession();
        sessions.TryGetReader(id, out var reader).Should().BeTrue();
        var token = sessions.GetLifetimeToken(id);

        sessions.Terminate(id).Should().BeTrue();

        token.IsCancellationRequested.Should().BeTrue("terminating a session cancels its lifetime token");
        reader.Completion.IsCompleted.Should().BeTrue("terminating a session completes its notification channel");
        sessions.TryGetReader(id, out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task Publisher_PublishProgress_AppendsWellFormedJsonRpcNotification()
    {
        var sessions = new McpSessionManager();
        var id = sessions.CreateSession();
        var publisher = CreatePublisher(sessions, out var drafts);

        (await publisher.PublishProgress(id, "job-9", progress: 42, total: 100, message: "Running")).Should().BeTrue();

        var draft = drafts.Should().ContainSingle().Subject;
        draft.ScopeKey.Should().Be(id);
        var root = draft.Payload;
        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("method").GetString().Should().Be("notifications/progress");
        var parameters = root.GetProperty("params");
        parameters.GetProperty("progressToken").GetString().Should().Be("job-9");
        parameters.GetProperty("progress").GetDouble().Should().Be(42);
        parameters.GetProperty("total").GetDouble().Should().Be(100);
        parameters.GetProperty("message").GetString().Should().Be("Running");
    }

    [UnitTest]
    public async Task Publisher_Broadcast_AppendsOneSharedNotification()
    {
        var sessions = new McpSessionManager();
        var first = sessions.CreateSession();
        var second = sessions.CreateSession();
        var publisher = CreatePublisher(sessions, out var drafts);

        (await publisher.BroadcastResourcesListChanged()).Should().Be(2);
        var draft = drafts.Should().ContainSingle().Subject;
        draft.ScopeKey.Should().Be("*");
        draft.Payload.GetProperty("method").GetString().Should().Be("notifications/resources/list_changed");
    }

    [UnitTest]
    public async Task Publisher_ConfiguredProviderFailure_DoesNotFallBackToLocalDelivery()
    {
        var sessions = new McpSessionManager();
        var sessionId = sessions.CreateSession();
        sessions.TryGetReader(sessionId, out var reader).Should().BeTrue();
        var provider = new RecordingFanoutProvider([], failAppend: true);
        var publisher = new McpNotificationPublisher(sessions, NullLogger<McpNotificationPublisher>.Instance, provider);

        Func<Task> progress = () => publisher.PublishProgress(sessionId, "job-1", 42, 100, null).AsTask();
        Func<Task> broadcast = () => publisher.BroadcastResourcesListChanged().AsTask();
        await progress.Should().ThrowAsync<IOException>();
        await broadcast.Should().ThrowAsync<IOException>();
        reader.TryRead(out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task ProgressBridge_PumpsCanonicalJobProgress_ToOwningSessionUntilTerminal()
    {
        var sessions = new McpSessionManager();
        var id = sessions.CreateSession();
        sessions.AssociateJob(id, "job-1");

        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobAsync("job-1", Principal, Arg.Any<CancellationToken>())
            .Returns(
                JobRecord(ExecutionJobStatus.Running, 10, "Provisioning"),
                JobRecord(ExecutionJobStatus.Running, 60, "Executing"),
                JobRecord(ExecutionJobStatus.Succeeded, 100, "Complete"));

        var publisher = CreatePublisher(sessions, out var drafts);
        var bridge = new McpJobProgressBridge(
            publisher,
            sessions,
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<McpJobProgressBridge>.Instance);

        await bridge.PumpAsync(id, "job-1", Principal, jobService, TimeSpan.Zero, CancellationToken.None);

        var progresses = new List<double>();
        foreach (var draft in drafts)
        {
            draft.ScopeKey.Should().Be(id);
            var root = draft.Payload;
            root.GetProperty("method").GetString().Should().Be("notifications/progress");
            root.GetProperty("params").GetProperty("progressToken").GetString().Should().Be("job-1");
            progresses.Add(root.GetProperty("params").GetProperty("progress").GetDouble());
        }

        var expectedProgress = new[] { 10d, 60d, 100d };
        progresses.Should().Equal(expectedProgress,
            "the bridge emits a frame on each observed progress change and a final terminal frame");
        await jobService.Received(3).GetJobAsync("job-1", Principal, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task GetStream_WithoutProvider_DeliversLocalNotificationAndPreservesSession()
    {
        var sessions = new McpSessionManager();
        var sessionId = sessions.CreateSession();
        sessions.TryEnqueue(sessionId, "local-notification").Should().BeTrue();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(sessions);
        services.AddSingleton(Options.Create(new McpOptions { ServerInitiatedStreamEnabled = true }));
        await using var provider = services.BuildServiceProvider();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Accept = "text/event-stream";
        context.Request.Headers[McpSessionManager.SessionHeaderName] = sessionId;
        context.Response.Body = output;

        var streaming = McpEndpointExtensions.HandleGetAsync(context, cancellation.Token);
        cancellation.Cancel();
        await streaming;

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        Encoding.UTF8.GetString(output.ToArray()).Should().Contain("data: local-notification");
        sessions.TryGetReader(sessionId, out _).Should().BeTrue();
    }

    [UnitTest]
    public async Task PumpSessionStream_WritesQueuedFramesAsSseMessageEvents()
    {
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.TryWrite("""{"method":"notifications/resources/list_changed"}""");
        channel.Writer.Complete();

        using var output = new MemoryStream();
        await McpEndpointExtensions.PumpSessionStreamAsync(output, channel.Reader, CancellationToken.None);

        var text = Encoding.UTF8.GetString(output.ToArray());
        text.Should().Contain("event: message\n");
        text.Should().Contain("""data: {"method":"notifications/resources/list_changed"}""");
        text.Should().EndWith("\n\n", "each SSE event is terminated by a blank line");
    }

    [UnitTest]
    public void NotificationModels_AreResolvableFromSourceGeneratedContext()
    {
        // AOT guard: the new wire types must be source-generated, not reflection
        // fallback, since the MCP path serializes them with the slice context.
        McpJsonContext.Default.McpJsonRpcNotification.Should().NotBeNull();
        McpJsonContext.Default.McpProgressNotificationParams.Should().NotBeNull();
    }

    private static ExecutionJobRecord JobRecord(ExecutionJobStatus status, double percent, string phase) => new()
    {
        OperationId = "job-1",
        Status = status,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        PercentComplete = percent,
        CurrentPhase = phase,
        Spec = new ExecutionJobSpec
        {
            TargetKind = default,
            Backend = "test",
            Kind = default,
            WorkloadName = "workload"
        }
    };
    private static McpNotificationPublisher CreatePublisher(McpSessionManager sessions, out List<RealtimeEventDraft> drafts)
    {
        var recorded = new List<RealtimeEventDraft>();
        drafts = recorded;
        var provider = new RecordingFanoutProvider(recorded);
        return new McpNotificationPublisher(sessions, NullLogger<McpNotificationPublisher>.Instance, provider);
    }

    private sealed class RecordingFanoutProvider(List<RealtimeEventDraft> drafts, bool failAppend = false) : IRealtimeFanoutProvider
    {
        public RealtimeProviderCapabilities Capabilities { get; } = new(true, true, true);

        public ValueTask<RealtimePosition> AppendAsync(
            RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken)
        {
            if (failAppend)
            {
                return ValueTask.FromException<RealtimePosition>(new IOException("Realtime append failed."));
            }

            drafts.Add(draft);
            return ValueTask.FromResult(new RealtimePosition(RealtimeFamily.Mcp, drafts.Count));
        }

        public void Enqueue(RealtimeEventDraft draft) => throw new NotSupportedException();

        public IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request) => throw new NotSupportedException();

        public ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
