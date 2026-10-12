// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    [InlineData("batch-event", BatchEvent, null)]
    [InlineData("backstop", "{}", null)]
    [InlineData("scheduled-tick", """{"kind":"JobReconciliation"}""", "JobReconciliation")]
    public async Task WorkNeedingTheDurableStore_OnARedisOffHost_AnswersTypedCapabilityUnavailable503(
        string selector,
        string payload,
        string? subject)
    {
        // honua-release run 38242272260 (aws-serverless, Redis-off): these answered 500.
        var targets = new RecordingTargets
        {
            Failure = new ControlPlaneEventDependencyUnavailableException(CapabilityUnavailableCodes.RedisDependency, subject),
        };

        var (status, body, contentType) = await InvokeWithContentTypeAsync(selector, payload, targets);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        contentType.Should().StartWith("application/problem+json");
        body.Should().Contain("\"missingDependency\":\"redis\"")
            .And.Contain($"\"code\":\"{CapabilityUnavailableCodes.ErrorCode}\"")
            .And.Contain(CapabilityUnavailableCodes.ProblemType)
            .And.Contain(CapabilityUnavailableCodes.DurableJobsCapability);
    }

    [UnitTest]
    public async Task WorkNeedingTheDurableStore_WithRedisPresentButUnentitled_ReportsTheLicence()
    {
        var services = new ServiceCollection().AddLogging();
        services.Configure<DurableJobSubstrateOptions>(options =>
        {
            options.RedisConfigured = true;
            options.RedisEntitled = false;
        });
        var targets = new RecordingTargets
        {
            Failure = new ControlPlaneEventDependencyUnavailableException(CapabilityUnavailableCodes.RedisDependency, null),
        };

        var (status, body, _) = await InvokeWithContentTypeAsync(
            "backstop", "{}", targets, requestServices: services.BuildServiceProvider());

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain($"\"code\":\"{CapabilityUnavailableCodes.EntitlementErrorCode}\"")
            .And.Contain(CapabilityUnavailableCodes.RedisCacheEntitlement)
            .And.NotContain("\"missingDependency\":\"redis\"");
    }

    [UnitTest]
    public async Task DependencyRefusal_IsLoggedAtWarningOncePerColdStart()
    {
        var logger = new RecordingLogger();
        var latch = new ColdStartWarningLatch();
        var targets = new RecordingTargets
        {
            Failure = new ControlPlaneEventDependencyUnavailableException(CapabilityUnavailableCodes.RedisDependency, null),
        };

        for (var i = 0; i < 3; i++)
        {
            var (status, _, _) = await InvokeWithContentTypeAsync("backstop", "{}", targets, logger: logger, latch: latch);
            status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        }

        logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("redis");
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [UnitTheory]
    [InlineData("batch-event", BatchEvent)]
    [InlineData("backstop", "{}")]
    [InlineData("scheduled-tick", """{"kind":"TemporaryFileCleanup"}""")]
    public async Task UnexpectedHandlerFailure_Answers500Problem_NamingTheExceptionType_WithoutAStack(
        string selector,
        string payload)
    {
        var targets = new RecordingTargets
        {
            Failure = new InvalidOperationException("No service for type 'Example' has been registered."),
        };

        var (status, body, contentType) = await InvokeWithContentTypeAsync(selector, payload, targets);

        status.Should().Be(StatusCodes.Status500InternalServerError);
        contentType.Should().StartWith("application/problem+json");
        body.Should().Contain("control_plane_lambda_handler_failed")
            .And.Contain("System.InvalidOperationException")
            .And.NotContain("   at ", "the stack trace stays in the function log");
    }

    [UnitTest]
    public async Task HostWithoutTargets_Answers503ControlPlaneNotReady()
    {
        var (status, body, _) = await InvokeWithContentTypeAsync("backstop", "{}", targets: null);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("control_plane_not_ready");
    }

    [UnitTheory]
    [InlineData("batch-event", "batch-event")]
    [InlineData("Reconcile", "batch-event")]
    [InlineData("backstop", "backstop")]
    [InlineData("scheduled-tick", "scheduled-tick")]
    [InlineData("sweep", null)]
    [InlineData("", null)]
    public void CanonicalHandler_MapsSelectorsAndAliases(string selector, string? expected)
        => ControlPlaneLambdaEventEndpoint.CanonicalHandler(selector).Should().Be(expected);

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
        RecordingTargets? targets,
        DegradedStartupContext? degraded,
        IPAddress? remoteIp = null,
        bool hasRemote = true,
        IServiceProvider? requestServices = null,
        ILogger? logger = null,
        ColdStartWarningLatch? latch = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = selector,
            })
            .Build();

        var context = new DefaultHttpContext
        {
            RequestServices = requestServices ?? new ServiceCollection().AddLogging().BuildServiceProvider(),
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
            logger ?? NullLogger.Instance,
            latch ?? new ColdStartWarningLatch(),
            degradedStartup: degraded);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(), context.Response.Headers);
    }

    private static async Task<(int Status, string Body, string? ContentType)> InvokeWithContentTypeAsync(
        string selector,
        string payload,
        RecordingTargets? targets,
        IPAddress? remoteIp = null,
        bool hasRemote = true,
        IServiceProvider? requestServices = null,
        ILogger? logger = null,
        ColdStartWarningLatch? latch = null)
    {
        var (status, body, headers) = await InvokeWithHeadersAsync(
            selector, payload, targets, null, remoteIp, hasRemote, requestServices, logger, latch);
        return (status, body, headers.ContentType.ToString());
    }

    private sealed class RecordingTargets : IControlPlaneLambdaEventTargets
    {
        public List<string> Calls { get; } = [];

        public bool TickThrows { get; init; }

        public Exception? Failure { get; init; }

        public Task ReconcileBatchJobAsync(string providerOperationId, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            Calls.Add($"reconcile:{providerOperationId}");
            return Task.CompletedTask;
        }

        public Task RunBackstopAsync(CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            Calls.Add("backstop");
            return Task.CompletedTask;
        }

        public Task RunScheduledTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken)
        {
            if (TickThrows)
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            ThrowIfFailing();
            Calls.Add($"tick:{kind}");
            return Task.CompletedTask;
        }

        private void ThrowIfFailing()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
