// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the managed GeoETL file sink executors
/// (sink.geojson-file, sink.quarantine). Each writes to a temp file and publishes a
/// result descriptor. No native dependency, no Docker.
/// </summary>
public sealed class FileSinkExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task GeoJsonFileSink_WritesFeatureCollectionFile()
    {
        var root = CreateOutputRoot();
        var relativePath = "out/sink.geojson";
        // Path.Combine args are relative test fixture fragments; no rooted-segment risk.
        string? path = null;
        var executor = new GeoJsonFileSinkExecutor(Options(root));
        try
        {
            var input = BuildInputUri(
                Feature(Point(1, 2), ("name", "a")),
                Feature(Point(3, 4), ("name", "b")));

            var (status, uri) = await RunAsync(
                executor, GeoJsonFileSinkExecutor.HandledProcessId, ("input", input), ("path", relativePath));

            status.Should().Be(ExecutionJobStatus.Succeeded);
            uri.Should().StartWith("data:application/json;base64,");
            path = Path.Join(root, Receipt(uri!).GetProperty("path").GetString());
            File.Exists(path).Should().BeTrue();

            path ??= Path.Join(root, Receipt(uri!).GetProperty("path").GetString());
            var written = await File.ReadAllTextAsync(path);
            var roundTrip = new GeoJsonReader().Read<FeatureCollection>(written);
            roundTrip.Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnitTest]
    public async Task GeoJsonFileSink_MissingPath_FailsCleanly()
    {
        var executor = new GeoJsonFileSinkExecutor(Options());
        var (status, _) = await RunAsync(
            executor, GeoJsonFileSinkExecutor.HandledProcessId, ("input", BuildInputUri(Feature(Point(0, 0)))));
        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task GeoJsonFileSink_AbsolutePath_FailsCleanly()
    {
        var root = CreateOutputRoot();
        var executor = new GeoJsonFileSinkExecutor(Options(root));
        try
        {
            // Path.Combine args are a temp-dir root plus a generated relative file name; no rooted-segment risk.
            var absolutePath = Path.Join(Path.GetTempPath(), $"honua-geoetl-sink-{Guid.NewGuid():N}.geojson");

            var (status, _) = await RunAsync(
                executor,
                GeoJsonFileSinkExecutor.HandledProcessId,
                ("input", BuildInputUri(Feature(Point(0, 0)))),
                ("path", absolutePath));

            status.Should().Be(ExecutionJobStatus.Failed);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnitTest]
    public async Task QuarantineSink_TagsRowsAndNeverThrows()
    {
        var root = CreateOutputRoot();
        var relativePath = "dlq/quarantine.geojson";
        // Path.Combine args are relative test fixture fragments; no rooted-segment risk.
        string? path = null;
        var executor = new QuarantineSinkExecutor(Options(root));
        try
        {
            var input = BuildInputUri(
                Feature(Point(0, 0), ("err", "bad-value")),
                Feature(Point(1, 1)));

            var (status, uri) = await RunAsync(
                executor, QuarantineSinkExecutor.HandledProcessId,
                ("input", input), ("path", relativePath), ("batchId", "batch-123"));

            status.Should().Be(ExecutionJobStatus.Succeeded);
            uri.Should().StartWith("data:application/json;base64,");

            path ??= Path.Join(root, Receipt(uri!).GetProperty("path").GetString());
            var written = await File.ReadAllTextAsync(path);
            var roundTrip = new GeoJsonReader().Read<FeatureCollection>(written);
            roundTrip.Should().HaveCount(2);
            roundTrip[0].Attributes.GetOptionalValue("_batch_id").Should().Be("batch-123");
            roundTrip[0].Attributes.Exists("_quarantine_reason").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnitTheory]
    [InlineData("sink.geojson-file")]
    [InlineData("sink.quarantine")]
    public async Task Submission_SameKeyAndPathAcrossOwnersAndTenants_CannotReplaceOtherOutputs(string processId)
    {
        var root = CreateOutputRoot();
        try
        {
            Directory.CreateDirectory(Path.Join(root, "shared"));
            var unownedPath = Path.Join(root, "shared", "result.geojson");
            await File.WriteAllTextAsync(unownedPath, "pre-existing unowned output");
            var records = new Dictionary<string, ExecutionJobRecord>();
            var store = Substitute.For<IExecutionJobStore>();
            store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(records.GetValueOrDefault(call.ArgAt<string>(0))));
            store.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(call => records.TryAdd(call.ArgAt<ExecutionJobRecord>(0).OperationId, call.ArgAt<ExecutionJobRecord>(0)));
            store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    records[call.ArgAt<ExecutionJobRecord>(0).OperationId] = call.ArgAt<ExecutionJobRecord>(0);
                    return true;
                });
            var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
            authorization.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
                .Returns(AccessDecision.Allowed());
            var approval = Substitute.For<IOperatorApprovalEvaluator>();
            approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
            var service = new GeoprocessingJobService(
                Substitute.For<IUniversalProgressStore>(), [Substitute.For<IJobCancellationNotifier>()],
                authorization, approval, new BuiltInProcessCatalog(), NullLogger<GeoprocessingJobService>.Instance,
                Options(root), store, Substitute.For<IJobQueue>());
            IJobExecutor executor = processId == "sink.quarantine"
                ? new QuarantineSinkExecutor(Options(root)) : new GeoJsonFileSinkExecutor(Options(root));
            var outputs = new Dictionary<string, string>();
            foreach (var (owner, tenant, value) in new[] { ("alice", "tenant-a", 10), ("bob", "tenant-a", 20), ("alice", "tenant-b", 30) })
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, owner), new Claim("tenant_id", tenant)], "Test"));
                var plan = new AnalysisPlan
                {
                    PlanId = "file-output",
                    IntentId = "file-output",
                    Steps = [new AnalysisPlanStep
                    {
                        StepId = "write",
                        Kind = AnalysisPlanStepKind.Geoprocess,
                        ProcessId = processId,
                        Inputs = new Dictionary<string, string>
                        {
                            ["input"] = BuildInputUri(Feature(Point(value, value), ("owner", owner))),
                            ["path"] = "shared/result.geojson",
                        },
                    }],
                };
                var admitted = await service.SubmitJobAsync(plan, "same-key", principal);
                var job = admitted with { Status = ExecutionJobStatus.Running, AttemptCount = 1 };
                var (result, receipt) = await Execute(job, executor);
                result.Status.Should().Be(ExecutionJobStatus.Succeeded);
                result.CompletedWithCommittedEffects.Should().BeTrue();
                var path = Path.Join(root, Receipt(receipt!).GetProperty("path").GetString());
                var bytes = await File.ReadAllTextAsync(path);
                var features = new GeoJsonReader().Read<FeatureCollection>(bytes);
                features.Single().Geometry.Coordinate.X.Should().Be(value);
                outputs.TryAdd(path, bytes).Should().BeTrue("each admitted owner/tenant gets an isolated destination");
                (await File.ReadAllTextAsync(unownedPath)).Should().Be("pre-existing unowned output");
                foreach (var previous in outputs)
                {
                    (await File.ReadAllTextAsync(previous.Key)).Should().Be(previous.Value);
                }

                var (repeated, _) = await Execute(job, executor);
                repeated.Status.Should().Be(ExecutionJobStatus.Failed);
                (await File.ReadAllTextAsync(path)).Should().Be(bytes);
                var (retry, retryReceipt) = await Execute(job with { AttemptCount = 2 }, executor);
                retry.Status.Should().Be(ExecutionJobStatus.Succeeded);
                Receipt(retryReceipt!).GetProperty("path").GetString().Should().NotBe(Receipt(receipt!).GetProperty("path").GetString());
                (await File.ReadAllTextAsync(path)).Should().Be(bytes);
            }

            outputs.Should().HaveCount(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnitTheory]
    [InlineData("sink.geojson-file")]
    [InlineData("sink.quarantine")]
    public async Task FileSink_LostLeaseBeforeCommit_DoesNotPublishOrLeaveOutput(string processId)
    {
        var root = CreateOutputRoot();
        try
        {
            IJobExecutor executor = processId == "sink.quarantine"
                ? new QuarantineSinkExecutor(Options(root)) : new GeoJsonFileSinkExecutor(Options(root));
            var context = Substitute.For<IJobExecutionContext>();
            context.ThrowIfExecutionLeaseLostAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new OperationCanceledException("lease lost")));
            await FluentActions.Awaiting(() => executor.ExecuteAsync(Record(processId), context, CancellationToken.None))
                .Should().ThrowAsync<OperationCanceledException>();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
            await context.DidNotReceive().RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnitTheory]
    [InlineData("sink.geojson-file")]
    [InlineData("sink.quarantine")]
    public async Task FileSink_CancellationAfterRename_PreservesCommittedReceiptAndSuccess(string processId)
    {
        var root = CreateOutputRoot();
        using var cancellation = new CancellationTokenSource();
        try
        {
            IJobExecutor executor = processId == "sink.quarantine"
                ? new QuarantineSinkExecutor(Options(root)) : new GeoJsonFileSinkExecutor(Options(root));
            var context = Substitute.For<IJobExecutionContext>();
            string? receipt = null;
            context.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Directory.EnumerateFiles(root, "*.geojson", SearchOption.AllDirectories).Should().ContainSingle();
                cancellation.Cancel();
                call.ArgAt<CancellationToken>(1).CanBeCanceled.Should().BeFalse();
                receipt = call.ArgAt<string>(0);
                return Task.CompletedTask;
            });
            var result = await executor.ExecuteAsync(Record(processId), context, cancellation.Token);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            result.CompletedWithCommittedEffects.Should().BeTrue();
            Receipt(receipt!).GetProperty("committed").GetBoolean().Should().BeTrue();
            var path = Path.Join(root, Receipt(receipt!).GetProperty("path").GetString());
            new GeoJsonReader().Read<FeatureCollection>(await File.ReadAllTextAsync(path))
                .Single().Geometry.Coordinate.X.Should().Be(42);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(JobExecutionResult Result, string? Receipt)> Execute(ExecutionJobRecord job, IJobExecutor executor)
    {
        var context = Substitute.For<IJobExecutionContext>();
        string? receipt = null;
        context.When(c => c.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => receipt = call.ArgAt<string>(0));
        return (await executor.ExecuteAsync(job, context, CancellationToken.None), receipt);
    }

    private static ExecutionJobRecord Record(string processId) => new()
    {
        OperationId = "file-sink-job",
        AttemptCount = 1,
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "file-sink-test",
            Parameters = new Dictionary<string, string>
            {
                [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
                ["protocolProcessId"] = processId,
                [ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0.input"] = BuildInputUri(Feature(Point(42, 7))),
                [ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0.path"] = "output.geojson",
            },
        },
    };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options(string? outputRootDirectory = null)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
            // Path.Combine args are a temp-dir root plus a literal relative folder name; no rooted-segment risk.
            OutputRootDirectory = outputRootDirectory
                ?? Path.Join(Path.GetTempPath(), "honua-geoprocessing-outputs")
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static JsonElement Receipt(string uri)
        => JsonDocument.Parse(Convert.FromBase64String(uri["data:application/json;base64,".Length..])).RootElement.Clone();

    private static string CreateOutputRoot()
    {
        // Path.Combine args are a temp-dir root plus a generated relative folder name; no rooted-segment risk.
        var root = Path.Join(Path.GetTempPath(), $"honua-geoetl-sink-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static Feature Feature(Geometry geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    private static string BuildInputUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        IJobExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri);
    }
}
