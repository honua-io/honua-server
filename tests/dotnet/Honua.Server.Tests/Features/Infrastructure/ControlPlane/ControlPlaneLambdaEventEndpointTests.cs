// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// The control-plane event Lambdas run the server image behind the AWS Lambda Web Adapter, which posts
/// every EventBridge / Scheduler invocation to <c>POST /events</c> from loopback. On the aws-serverless
/// cell (honua-release e2e-cloud-aws run 38133126673) that request was rejected by host validation and
/// no route existed, so reconcile, backstop and scheduled ticks never ran.
/// </summary>
public sealed class ControlPlaneLambdaEventEndpointTests
{
    private const string BatchEvent = """
        {"version":"0","id":"e1","detail-type":"Batch Job State Change","source":"aws.batch",
         "detail":{"jobId":"batch-job-123","status":"SUCCEEDED"}}
        """;

    [UnitTheory]
    [InlineData("batch-event")]
    [InlineData("reconcile")]
    [InlineData("BATCH-EVENT")]
    public async Task BatchEventSelector_ReconcilesTheEventsJobId(string selector)
    {
        var targets = new RecordingTargets();

        var (status, body) = await InvokeAsync(selector, BatchEvent, targets);

        status.Should().Be(StatusCodes.Status200OK);
        targets.Calls.Should().Equal("reconcile:batch-job-123");
        body.Should().Contain("\"status\":\"ran\"");
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"source":"aws.ecs","detail-type":"Batch Job State Change","detail":{"jobId":"x"}}""")]
    [InlineData("""{"source":"aws.batch","detail-type":"Batch Job State Change","detail":{}}""")]
    public async Task BatchEventSelector_WithoutAnActionableJob_IsANoOp(string payload)
    {
        var targets = new RecordingTargets();

        var (status, body) = await InvokeAsync("batch-event", payload, targets);

        status.Should().Be(StatusCodes.Status200OK);
        targets.Calls.Should().BeEmpty();
        body.Should().Contain("\"status\":\"ignored\"");
    }

    [UnitTest]
    public async Task BackstopSelector_RunsOneBackstop_WhateverTheSchedulerSends()
    {
        var targets = new RecordingTargets();

        var (status, _) = await InvokeAsync("backstop", """{"anything":"opaque"}""", targets);

        status.Should().Be(StatusCodes.Status200OK);
        targets.Calls.Should().Equal("backstop");
    }

    [UnitTest]
    public async Task ScheduledTickSelector_RunsTheNamedTick()
    {
        var targets = new RecordingTargets();

        var (status, body) = await InvokeAsync("scheduled-tick", """{"kind":"jobReconciliation"}""", targets);

        status.Should().Be(StatusCodes.Status200OK);
        targets.Calls.Should().Equal("tick:JobReconciliation");
        body.Should().Contain("JobReconciliation");
    }

    [UnitTheory]
    [InlineData("{}", "scheduled_tick_kind_required")]
    [InlineData("""{"kind":"NoSuchTick"}""", "scheduled_tick_kind_unknown")]
    [InlineData("""{"kind":"42"}""", "scheduled_tick_kind_unknown")]
    public async Task ScheduledTickSelector_WithoutAKnownKind_Returns400(string payload, string code)
    {
        var targets = new RecordingTargets();

        var (status, body) = await InvokeAsync("scheduled-tick", payload, targets);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain(code);
        targets.Calls.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ScheduledTickSelector_KindWithNoHandlerInThisDeployment_Returns503()
    {
        var targets = new RecordingTargets { TickThrows = true };

        var (status, body) = await InvokeAsync("scheduled-tick", """{"kind":"DigestFlush"}""", targets);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("scheduled_tick_kind_unavailable");
    }

    [UnitTest]
    public async Task UnknownSelector_Returns400Problem_AndRunsNothing()
    {
        var targets = new RecordingTargets();

        var (status, body) = await InvokeAsync("sweep-everything", BatchEvent, targets);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("control_plane_lambda_handler_unknown");
        targets.Calls.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("batch-event", null)]
    [InlineData("backstop", "schema-floor")]
    [InlineData("scheduled-tick", "schema-floor")]
    public async Task DegradedHost_Returns503NotReady_SoTheEventIsRetried(string selector, string? reasonCode)
    {
        // honua-server#5916: cp-tick and cp-backstop cold-started before migrations were applied and
        // exited. A degraded host must answer a typed 503 instead of running the handler.
        var targets = new RecordingTargets();
        var degraded = new DegradedStartupContext();
        degraded.MarkDegraded(
            migrationsPending: false,
            migrationsAssembly: null,
            reason: "schema guard: migration '001_CreateRasterTables.sql' not applied",
            schemaGuardPending: true,
            reasonCode: reasonCode);
        var payload = selector == "scheduled-tick" ? """{"kind":"jobReconciliation"}""" : BatchEvent;

        var (status, body, headers) = await InvokeWithHeadersAsync(selector, payload, targets, degraded);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain(ControlPlaneNotReadyGuard.ProblemCode);
        headers[ReadinessReasonCodes.HeaderName].ToString()
            .Should().Be(reasonCode ?? ReadinessReasonCodes.DatabaseStartupDegraded);
        targets.Calls.Should().BeEmpty();
    }

    [UnitTest]
    public async Task RecoveredHost_RunsTheHandler()
    {
        var targets = new RecordingTargets();
        var degraded = new DegradedStartupContext();
        degraded.MarkDegraded(false, null, "schema guard", schemaGuardPending: true, reasonCode: ReadinessReasonCodes.SchemaFloor);
        degraded.MarkRecovered();

        var (status, _, _) = await InvokeWithHeadersAsync("backstop", "{}", targets, degraded);

        status.Should().Be(StatusCodes.Status200OK);
        targets.Calls.Should().Equal("backstop");
    }

    [UnitTheory]
    [InlineData("203.0.113.9")]
    [InlineData("none")]
    public async Task NonLoopbackCaller_Returns403_AndRunsNothing(string remoteIp)
    {
        var targets = new RecordingTargets();
        var remote = remoteIp == "none" ? null : IPAddress.Parse(remoteIp);

        var (status, _) = await InvokeAsync("backstop", "{}", targets, remote, hasRemote: remote is not null);

        status.Should().Be(StatusCodes.Status403Forbidden);
        targets.Calls.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Ipv4MappedLoopbackCaller_IsAccepted()
    {
        var targets = new RecordingTargets();

        var (status, _) = await InvokeAsync("backstop", "{}", targets, IPAddress.Loopback.MapToIPv6());

        status.Should().Be(StatusCodes.Status200OK);
        targets.Calls.Should().Equal("backstop");
    }

    [UnitTheory]
    [InlineData(null, "/events")]
    [InlineData("", "/events")]
    [InlineData("/lambda-events", "/lambda-events")]
    [InlineData("lambda-events", "/lambda-events")]
    public void ResolvePath_FollowsTheAdaptersPassThroughPath(string? configured, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaEventEndpoint.PassThroughPathKey] = configured,
            })
            .Build();

        ControlPlaneLambdaEventEndpoint.ResolvePath(configuration).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(null, false)]
    [InlineData("scheduled-tick", true)]
    public async Task Route_IsMappedOnlyOnAnEventHandlerHost(string? selector, bool mapped)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = selector,
            })
            .Build();
        await using var app = WebApplication.CreateSlimBuilder().Build();

        app.MapControlPlaneLambdaEventEndpoint(configuration);

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText);
        if (mapped)
        {
            routes.Should().ContainSingle().Which.Should().Be("/events");
        }
        else
        {
            routes.Should().BeEmpty("the API host must not expose the adapter route");
        }
    }

    private static async Task<(int Status, string Body)> InvokeAsync(
        string selector,
        string payload,
        RecordingTargets targets,
        IPAddress? remoteIp = null,
        bool hasRemote = true)
    {
        var (status, body, _) = await InvokeWithHeadersAsync(selector, payload, targets, null, remoteIp, hasRemote);
        return (status, body);
    }

    private static async Task<(int Status, string Body, IHeaderDictionary Headers)> InvokeWithHeadersAsync(
        string selector,
        string payload,
        RecordingTargets targets,
        DegradedStartupContext? degraded,
        IPAddress? remoteIp = null,
        bool hasRemote = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = selector,
            })
            .Build();

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/events";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = hasRemote ? remoteIp ?? IPAddress.Loopback : null;
        context.Response.Body = new MemoryStream();

        var result = await ControlPlaneLambdaEventEndpoint.HandleAsync(
            context,
            configuration,
            targets,
            NullLogger.Instance,
            degraded);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(), context.Response.Headers);
    }

    private sealed class RecordingTargets : IControlPlaneLambdaEventTargets
    {
        public List<string> Calls { get; } = [];

        public bool TickThrows { get; init; }

        public Task ReconcileBatchJobAsync(string providerOperationId, CancellationToken cancellationToken)
        {
            Calls.Add($"reconcile:{providerOperationId}");
            return Task.CompletedTask;
        }

        public Task RunBackstopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("backstop");
            return Task.CompletedTask;
        }

        public Task RunScheduledTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken)
        {
            if (TickThrows)
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            Calls.Add($"tick:{kind}");
            return Task.CompletedTask;
        }
    }
}
