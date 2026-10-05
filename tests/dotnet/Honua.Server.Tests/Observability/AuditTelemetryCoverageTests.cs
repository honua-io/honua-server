// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Geoprocessing;
using Honua.Infrastructure.Middleware;
using Honua.Server.Features.Admin.Services;
using Honua.ServiceDefaults;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Proto = Geospatial.V1;
using Status = Grpc.Core.Status;
using StatusCode = Grpc.Core.StatusCode;

namespace Honua.Server.Tests.Observability;

/// <summary>
/// Regression coverage for the server-20261004 telemetry audit (A3-001..A3-004): every
/// assertion observes a recorded serving sample, an emitted span, or an exported OTLP payload
/// rather than inspecting configuration.
/// </summary>
[Collection("HonuaTelemetry")]
[Protocol(TestProtocols.TestQuality)]
public sealed class AuditTelemetryCoverageTests
{
    private const string ServingInstrument = "honua_serving_request_duration_ms";

    // ── A3-001 / #5473: MCP and gRPC serving samples ───────────────────────────────────────

    [Theory]
    [InlineData("POST", 200, "mcp.rpc", "2xx", false)]
    [InlineData("GET", 200, "mcp.stream", "2xx", false)]
    [InlineData("DELETE", 204, "mcp.session.delete", "2xx", false)]
    [InlineData("POST", 503, "mcp.rpc", "5xx", true)]
    [Trait("Tier", "Fast")]
    public async Task McpRequest_RecordsCanonicalServingSampleAndOpsHealthReservoir(
        string method, int statusCode, string operation, string statusClass, bool isServerError)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.MapMethods("/mcp", [method], (HttpContext context) =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var metrics = new ServingMetrics();
        var before = ReservoirCounts(HonuaTelemetry.Protocols.Mcp);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/mcp"));
        await app.StopAsync();

        Assert.Equal(statusCode, (int)response.StatusCode);
        var sample = Assert.Single(metrics.Samples, s => Equals(s.Tags[HonuaTelemetry.Tags.Protocol], HonuaTelemetry.Protocols.Mcp));
        Assert.Equal(operation, sample.Tags[HonuaTelemetry.Tags.Operation]);
        Assert.Equal(statusClass, sample.Tags["status_class"]);
        var after = ReservoirCounts(HonuaTelemetry.Protocols.Mcp);
        Assert.Equal(before.Requests + 1, after.Requests);
        Assert.Equal(before.Errors + (isServerError ? 1 : 0), after.Errors);
    }

    [Theory]
    [InlineData("ok", StatusCode.OK, "2xx", false)]
    [InlineData("missing", StatusCode.NotFound, "4xx", false)]
    [InlineData("denied", StatusCode.PermissionDenied, "4xx", false)]
    [InlineData("boom", StatusCode.Internal, "5xx", true)]
    [InlineData("down", StatusCode.Unavailable, "5xx", true)]
    [Trait("Tier", "Fast")]
    public async Task GrpcWebCall_RecordsServingSampleClassifiedByGrpcStatusNotHttp200(
        string datasetId, StatusCode expectedStatus, string statusClass, bool isServerError)
    {
        await using var app = await StartGrpcAppAsync();
        using var channel = GrpcChannel.ForAddress(app.GetTestServer().BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, app.GetTestServer().CreateHandler()),
        });
        var client = new Proto.ElevationService.ElevationServiceClient(channel);
        using var metrics = new ServingMetrics();
        var before = ReservoirCounts(HonuaTelemetry.Protocols.Grpc);

        var status = StatusCode.OK;
        try
        {
            await client.GetElevationAsync(new Proto.GetElevationRequest { DatasetId = datasetId });
        }
        catch (RpcException ex)
        {
            status = ex.StatusCode;
        }

        await app.StopAsync();

        Assert.Equal(expectedStatus, status);
        var sample = Assert.Single(metrics.Samples, s => Equals(s.Tags[HonuaTelemetry.Tags.Protocol], HonuaTelemetry.Protocols.Grpc));
        Assert.Equal("geospatial.v1.ElevationService/GetElevation", sample.Tags[HonuaTelemetry.Tags.Operation]);
        Assert.Equal(statusClass, sample.Tags["status_class"]);
        var after = ReservoirCounts(HonuaTelemetry.Protocols.Grpc);
        Assert.Equal(before.Requests + 1, after.Requests);
        Assert.Equal(before.Errors + (isServerError ? 1 : 0), after.Errors);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task GrpcCallToUnregisteredMethod_UsesBoundedOperationName()
    {
        await using var app = await StartGrpcAppAsync();
        using var client = app.GetTestClient();
        using var metrics = new ServingMetrics();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/geospatial.v1.ElevationService/AttackerChosenName42")
        {
            Content = new ByteArrayContent(new byte[5]),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc-web");

        using var response = await client.SendAsync(request);
        await app.StopAsync();

        var sample = Assert.Single(metrics.Samples, s => Equals(s.Tags[HonuaTelemetry.Tags.Protocol], HonuaTelemetry.Protocols.Grpc));
        Assert.Equal("unknown", sample.Tags[HonuaTelemetry.Tags.Operation]);
        Assert.Equal("5xx", sample.Tags["status_class"]);
    }

    // ── A3-002 / #5474: durable GP execution keeps submission trace linkage ────────────────

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task DurableGeoprocessingJob_CarriesSubmissionCorrelationAndLinksWorkerSpan()
    {
        using var listener = ListenTo(HonuaTelemetry.ServiceName, "Honua.AuditTelemetryCoverage.Submit");
        using var submitSource = new ActivitySource("Honua.AuditTelemetryCoverage.Submit");
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateGeoprocessingJobService(jobStore);

        ActivityContext submissionContext;
        ExecutionJobRecord submitted;
        using (var submission = submitSource.StartActivity("POST /ogc/processes/execution", ActivityKind.Server))
        {
            Assert.NotNull(submission);
            submission.SetBaggage("correlation.id", "corr-a3-002");
            submissionContext = submission.Context;
            submitted = await service.SubmitJobAsync(CreatePlan(), null, CreatePrincipal());
        }

        Assert.Equal("corr-a3-002", submitted.Audit.CorrelationId);

        // Durable hop: the worker reads the record another node persisted, with no ambient trace.
        var persisted = JsonSerializer.Serialize(submitted, ControlPlaneJsonContext.Default.ExecutionJobRecord);
        Assert.Contains(submissionContext.TraceId.ToHexString(), persisted, StringComparison.Ordinal);
        var dequeued = JsonSerializer.Deserialize(persisted, ControlPlaneJsonContext.Default.ExecutionJobRecord)!;

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var worker = ControlPlaneTelemetry.StartExecutionActivity(
                ControlPlaneTelemetry.Activities.ExecutionRun, "run", dequeued);

            Assert.NotNull(worker);
            Assert.NotEqual(submissionContext.TraceId, worker.TraceId);
            var link = Assert.Single(worker.Links);
            Assert.Equal(submissionContext.TraceId, link.Context.TraceId);
            Assert.Equal(submissionContext.SpanId, link.Context.SpanId);
            Assert.Equal("corr-a3-002", worker.GetTagItem(HonuaTelemetry.Tags.CorrelationId));
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ExecutionActivity_InsideSubmittingRequest_KeepsRequestAsParentWithoutSelfLink()
    {
        // Submission-side backend calls (remote StartAsync, observe, cancel) run inside the
        // request: their span must stay a child of it rather than become a linked root.
        using var listener = ListenTo(HonuaTelemetry.ServiceName, "Honua.AuditTelemetryCoverage.Submit");
        using var submitSource = new ActivitySource("Honua.AuditTelemetryCoverage.Submit");
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateGeoprocessingJobService(jobStore);

        using var request = submitSource.StartActivity("POST /ogc/processes/execution", ActivityKind.Server);
        Assert.NotNull(request);
        var submitted = await service.SubmitJobAsync(CreatePlan(), null, CreatePrincipal());

        using var backendCall = ControlPlaneTelemetry.StartExecutionActivity(
            ControlPlaneTelemetry.Activities.ExecutionRun, "start", submitted);

        Assert.NotNull(backendCall);
        Assert.Equal(request.TraceId, backendCall.TraceId);
        Assert.Equal(request.SpanId, backendCall.ParentSpanId);
        Assert.Empty(backendCall.Links);
    }

    // ── A3-003 / #5475: span-event exception details and the event ceiling ────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task DeadlockRetryEvent_FollowsExceptionDetailExportPolicy(bool exportDetails)
    {
        var events = new ConcurrentQueue<ActivityEvent>();
        using var listener = ListenTo(HonuaTelemetry.ServiceName);
        listener.ActivityStopped = activity =>
        {
            if (activity.OperationName == "honua.db.deadlock_retry")
            {
                foreach (var activityEvent in activity.Events)
                {
                    events.Enqueue(activityEvent);
                }
            }
        };
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=a3_003_unused");
        var provider = new PostgresDatabaseConnectionProvider(
            dataSource, NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        var attempts = 0;

        try
        {
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails, includeStackTraces: false, maxDetailLength: 128);
            var result = await provider.ExecuteWithDeadlockRetryAsync(() =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new PostgresException(
                        "deadlock detected password=hunter2 contact=dba@example.com",
                        "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
                }

                return Task.FromResult(42);
            });

            Assert.Equal(42, result);
        }
        finally
        {
            HonuaTelemetry.ConfigureExceptionRecording(exportDetails: false, includeStackTraces: false);
        }

        var retry = Assert.Single(events, e => e.Name == "deadlock_retry");
        var message = retry.Tags.FirstOrDefault(tag => tag.Key == "error.message").Value as string;
        Assert.DoesNotContain(retry.Tags, tag => tag.Value is string text && text.Contains("hunter2", StringComparison.Ordinal));
        Assert.DoesNotContain(retry.Tags, tag => tag.Value is string text && text.Contains("dba@example.com", StringComparison.Ordinal));
        if (exportDetails)
        {
            Assert.NotNull(message);
            Assert.Contains("password=***", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(message);
        }

        Assert.Equal(PostgresErrorCodes.DeadlockDetected,
            retry.Tags.FirstOrDefault(tag => tag.Key == "db.response.status_code").Value);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task OtlpExport_HonorsConfiguredMaxEventsPerSpan()
    {
        var payloads = new ConcurrentQueue<byte[]>();
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = receiverBuilder.Build();
        receiver.MapPost("/v1/traces", async (HttpContext context) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            payloads.Enqueue(buffer.ToArray());
            context.Response.ContentType = "application/x-protobuf";
        });
        await receiver.StartAsync();
        var receiverUrl = receiver.Urls.Single();

        var builder = CreateTracedHostBuilder(new Dictionary<string, string?>
        {
            ["Tracing:OtlpEndpoint"] = receiverUrl + "/v1/traces",
            ["Tracing:MaxEventsPerSpan"] = "2",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });
        using (var host = builder.Build())
        {
            var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            using (var span = HonuaTelemetry.ActivitySource.StartActivity("a3-003.event-ceiling"))
            {
                Assert.NotNull(span);
                Assert.True(span.Recorded);
                for (var i = 0; i < 5; i++)
                {
                    span.AddEvent(new ActivityEvent($"a3-003-event-{i}"));
                }
            }

            Assert.True(tracerProvider.ForceFlush(10_000));
        }

        await receiver.StopAsync();
        var exported = Assert.Single(
            payloads.Select(payload => Encoding.UTF8.GetString(payload)),
            text => text.Contains("a3-003.event-ceiling", StringComparison.Ordinal));
        Assert.Contains("a3-003-event-0", exported, StringComparison.Ordinal);
        Assert.Contains("a3-003-event-1", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("a3-003-event-2", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("a3-003-event-4", exported, StringComparison.Ordinal);
    }

    // ── A3-004 / #5476: every declared ActivitySource reaches the server tracer ───────────

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task ReplicaConflictResolveSpan_IsCapturedByServerTracer()
    {
        var captured = new ConcurrentQueue<Activity>();
        var builder = CreateTracedHostBuilder();
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddProcessor(new CapturingProcessor(captured)));
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<TracerProvider>();
        var service = new ReplicaConflictResolutionService(
            Substitute.For<IReplicaConflictRepository>(),
            Substitute.For<IChangeTracker>(),
            Substitute.For<IAuditLog>(),
            NullLogger<ReplicaConflictResolutionService>.Instance);

        var result = await service.ResolveAsync(new ReplicaConflictResolutionServiceRequest(
            "replica-a3-004", "conflict-a3-004", ReplicaConflictResolutionAction.AcceptClient, "acceptClient",
            new ReplicaConflictResolutionInputs(null, null), "operator", "corr-a3-004"));

        Assert.Equal(ReplicaConflictResolutionStatus.NotFound, result.Status);
        var span = Assert.Single(captured, activity => activity.OperationName == "replicaconflict.resolve");
        Assert.Equal("Honua.Server.ReplicaConflicts", span.Source.Name);
        Assert.Equal("conflict-a3-004", span.GetTagItem("replicaconflict.id"));
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public void EveryDeclaredHonuaActivitySource_IsSubscribedByServerTracer()
    {
        var declared = DiscoverDeclaredActivitySourceNames();
        Assert.Contains("Honua.Server.ReplicaConflicts", declared);
        Assert.Contains(HonuaTelemetry.ServiceName, declared);

        var builder = CreateTracedHostBuilder();
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<TracerProvider>();

        var unsubscribed = declared
            .Where(name =>
            {
                using var probe = new ActivitySource(name);
                return !probe.HasListeners();
            })
            .ToList();

        Assert.True(unsubscribed.Count == 0,
            "ActivitySources declared under src/ but not subscribed by ServiceDefaults' tracer (their spans are dropped): "
            + string.Join(", ", unsubscribed));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    // The server's telemetry defaults on a hermetic configuration (no appsettings*.json from the
    // test output folder, whose Tracing:SamplingRatio is 0.1) with adaptive sampling off, so every
    // span the test emits is recorded.
    private static HostApplicationBuilder CreateTracedHostBuilder(Dictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Test",
            DisableDefaults = true,
        });
        var configuration = new Dictionary<string, string?> { ["AdaptiveSampling:Enabled"] = "false" };
        foreach (var setting in settings ?? [])
        {
            configuration[setting.Key] = setting.Value;
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddTelemetryDefaults();
        return builder;
    }

    private static async Task<WebApplication> StartGrpcAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
        app.UseGrpcServingStatusCapture();
        app.MapGrpcService<StatusElevationService>();
        await app.StartAsync();
        return app;
    }

    private static (long Requests, long Errors) ReservoirCounts(string protocol)
    {
        var snapshot = HonuaTelemetry.GetServingLatencySnapshot().Protocols
            .SingleOrDefault(p => p.Protocol == protocol);
        return (snapshot?.RequestCount ?? 0, snapshot?.ErrorCount ?? 0);
    }

    private static ActivityListener ListenTo(params string[] sourceNames)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => sourceNames.Contains(source.Name, StringComparer.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static GeoprocessingJobService CreateGeoprocessingJobService(IExecutionJobStore jobStore)
    {
        var authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var approvalEvaluator = Substitute.For<IOperatorApprovalEvaluator>();
        approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        return new GeoprocessingJobService(
            Substitute.For<IUniversalProgressStore>(), [Substitute.For<IJobCancellationNotifier>()],
            authEvaluator, approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions()),
            jobStore, Substitute.For<IJobQueue>(),
            resultPackageStore: Substitute.For<IGeoprocessingResultPackageStore>());
    }

    private static AnalysisPlan CreatePlan() => new()
    {
        PlanId = "plan-a3-002",
        IntentId = "intent-a3-002",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-1",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "geometry.buffer",
                Inputs = new Dictionary<string, string>
                {
                    ["wkb"] = "AAAA",
                    ["srid"] = "4326",
                    ["distance"] = "100",
                },
            },
        ],
    };

    private static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Display Name"),
                new Claim(ClaimTypes.NameIdentifier, "subject-a3-002"),
            ], "Test"));

    private static List<string> DiscoverDeclaredActivitySourceNames()
    {
        var srcRoot = RepositoryPaths.Resolve("src");
        var declaration = new Regex(
            "ActivitySource\\s+\\w+\\s*=\\s*new(?:\\s+(?:System\\.Diagnostics\\.)?ActivitySource)?\\(\\s*\"(?<name>[^\"]+)\"",
            RegexOptions.CultureInvariant);
        var sourceNameConstant = new Regex(
            "const\\s+string\\s+\\w*SourceName\\s*=\\s*\"(?<name>[^\"]+)\"",
            RegexOptions.CultureInvariant);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains("ActivitySource", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in declaration.Matches(text))
            {
                names.Add(match.Groups["name"].Value);
            }

            foreach (Match match in sourceNameConstant.Matches(text))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names.ToList();
    }

    private sealed class StatusElevationService : Proto.ElevationService.ElevationServiceBase
    {
        public override Task<Proto.GetElevationResponse> GetElevation(Proto.GetElevationRequest request, ServerCallContext context)
            => request.DatasetId switch
            {
                "missing" => throw new RpcException(new Status(StatusCode.NotFound, "no such dataset")),
                "denied" => throw new RpcException(new Status(StatusCode.PermissionDenied, "denied")),
                "boom" => throw new RpcException(new Status(StatusCode.Internal, "failed")),
                "down" => throw new RpcException(new Status(StatusCode.Unavailable, "unavailable")),
                _ => Task.FromResult(new Proto.GetElevationResponse()),
            };
    }

    private sealed class CapturingProcessor(ConcurrentQueue<Activity> captured) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => captured.Enqueue(data);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record Sample(string Name, IReadOnlyDictionary<string, object?> Tags);

    private sealed class ServingMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ServingMetrics()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == HonuaTelemetry.ServiceName && instrument.Name == ServingInstrument)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
                Samples.Enqueue(new Sample(instrument.Name, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _listener.Start();
        }

        public ConcurrentQueue<Sample> Samples { get; } = new();

        public void Dispose() => _listener.Dispose();
    }
}
