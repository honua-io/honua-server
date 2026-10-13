// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
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
    public void Publisher_PublishProgress_EnqueuesWellFormedJsonRpcNotification()
    {
        var sessions = new McpSessionManager();
        var id = sessions.CreateSession();
        var publisher = new McpNotificationPublisher(sessions, NullLogger<McpNotificationPublisher>.Instance);

        publisher.PublishProgress(id, "job-9", progress: 42, total: 100, message: "Running").Should().BeTrue();

        sessions.TryGetReader(id, out var reader).Should().BeTrue();
        reader.TryRead(out var frame).Should().BeTrue();

        using var document = JsonDocument.Parse(frame!);
        var root = document.RootElement;
        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("method").GetString().Should().Be("notifications/progress");
        var parameters = root.GetProperty("params");
        parameters.GetProperty("progressToken").GetString().Should().Be("job-9");
        parameters.GetProperty("progress").GetDouble().Should().Be(42);
        parameters.GetProperty("total").GetDouble().Should().Be(100);
        parameters.GetProperty("message").GetString().Should().Be("Running");
    }

    [UnitTest]
    public void Publisher_Broadcast_ReachesEveryActiveSession()
    {
        var sessions = new McpSessionManager();
        var first = sessions.CreateSession();
        var second = sessions.CreateSession();
        var publisher = new McpNotificationPublisher(sessions, NullLogger<McpNotificationPublisher>.Instance);

        publisher.BroadcastResourcesListChanged().Should().Be(2);

        foreach (var id in new[] { first, second })
        {
            sessions.TryGetReader(id, out var reader).Should().BeTrue();
            reader.TryRead(out var frame).Should().BeTrue();
            using var document = JsonDocument.Parse(frame!);
            document.RootElement.GetProperty("method").GetString()
                .Should().Be("notifications/resources/list_changed");
        }
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

        var publisher = new McpNotificationPublisher(sessions, NullLogger<McpNotificationPublisher>.Instance);
        var bridge = new McpJobProgressBridge(
            publisher,
            sessions,
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<McpJobProgressBridge>.Instance);

        await bridge.PumpAsync(id, "job-1", Principal, jobService, TimeSpan.Zero, CancellationToken.None);

        sessions.TryGetReader(id, out var reader).Should().BeTrue();
        var progresses = new List<double>();
        while (reader.TryRead(out var frame))
        {
            using var document = JsonDocument.Parse(frame!);
            var root = document.RootElement;
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
}
