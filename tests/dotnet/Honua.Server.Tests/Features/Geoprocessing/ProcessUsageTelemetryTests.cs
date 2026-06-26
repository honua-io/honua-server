// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Honua.Server.Tests.Features.Geoprocessing.Execution.ManagedExecutorTestHarness;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit coverage for the usage-ranked GP tool tiering store (#2144): the in-memory
/// telemetry ranks processes by invocation count with stable tie-breaking, reflects
/// newly recorded invocations on the next read, and is fed by the geoprocessing
/// dispatcher on every dispatched job.
/// </summary>
public sealed class ProcessUsageTelemetryTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [UnitTest]
    public void Ranking_OrdersByInvocationCountDescending()
    {
        var telemetry = new InMemoryProcessUsageTelemetry(new MutableTimeProvider());

        telemetry.RecordInvocation("overlay.clip", succeeded: true);
        telemetry.RecordInvocation("overlay.clip", succeeded: true);
        telemetry.RecordInvocation("overlay.clip", succeeded: false);
        telemetry.RecordInvocation("statistics.frequency", succeeded: true);

        var ranking = telemetry.GetRanking();

        ranking.Should().HaveCount(2);
        ranking[0].ProcessId.Should().Be("overlay.clip");
        ranking[0].InvocationCount.Should().Be(3);
        ranking[0].SuccessCount.Should().Be(2);
        ranking[0].FailureCount.Should().Be(1);
        ranking[1].ProcessId.Should().Be("statistics.frequency");
    }

    [UnitTest]
    public void Ranking_NewInvocationsChangeOrderOnNextRead()
    {
        var telemetry = new InMemoryProcessUsageTelemetry(new MutableTimeProvider());

        telemetry.RecordInvocation("overlay.clip", succeeded: true);
        telemetry.RecordInvocation("proximity.near", succeeded: true);
        telemetry.RecordInvocation("proximity.near", succeeded: true);

        telemetry.GetRanking()[0].ProcessId.Should().Be("proximity.near", "it leads with 2 invocations");

        // Push overlay.clip ahead; the next read reflects it (real store, not a stub).
        telemetry.RecordInvocation("overlay.clip", succeeded: true);
        telemetry.RecordInvocation("overlay.clip", succeeded: true);

        telemetry.GetRanking()[0].ProcessId.Should().Be("overlay.clip", "the freshly recorded invocations reorder the ranking");
    }

    [UnitTest]
    public void Ranking_TieBrokenByMostRecentThenProcessId()
    {
        var clock = new MutableTimeProvider();
        var telemetry = new InMemoryProcessUsageTelemetry(clock);

        clock.Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        telemetry.RecordInvocation("overlay.erase", succeeded: true);

        clock.Now = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        telemetry.RecordInvocation("overlay.union", succeeded: true);

        var ranking = telemetry.GetRanking();

        ranking.Should().HaveCount(2);
        ranking[0].ProcessId.Should().Be("overlay.union", "equal counts tie-break by most-recent use");
    }

    [UnitTest]
    public void Ranking_LimitTruncatesResult()
    {
        var telemetry = new InMemoryProcessUsageTelemetry(new MutableTimeProvider());
        telemetry.RecordInvocation("a", succeeded: true);
        telemetry.RecordInvocation("a", succeeded: true);
        telemetry.RecordInvocation("b", succeeded: true);

        telemetry.GetRanking(limit: 1).Should().ContainSingle().Which.ProcessId.Should().Be("a");
    }

    [UnitTest]
    public async Task Dispatcher_RecordsInvocationForExecutedProcess()
    {
        var telemetry = new InMemoryProcessUsageTelemetry(new MutableTimeProvider());
        IProcessExecutor[] executors = { new OverlayMergeExecutor(Options()) };
        var dispatcher = new GeoprocessingDispatchJobExecutor(
            executors,
            NullLogger<GeoprocessingDispatchJobExecutor>.Instance,
            telemetry);

        var (status, _) = await RunDispatchAsync(
            dispatcher,
            OverlayMergeExecutor.HandledProcessId,
            ("input", Uri(Feature(Point(0, 0)))),
            ("merge", Uri(Feature(Point(1, 1)))));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var ranking = telemetry.GetRanking();
        ranking.Should().ContainSingle();
        ranking[0].ProcessId.Should().Be(OverlayMergeExecutor.HandledProcessId);
        ranking[0].InvocationCount.Should().Be(1);
        ranking[0].SuccessCount.Should().Be(1);
    }

    // Runs a job through the dispatcher (IJobExecutor) rather than a single executor,
    // exercising the telemetry recording hook on the real dispatch path.
    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunDispatchAsync(
        GeoprocessingDispatchJobExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = NSubstitute.Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<CancellationToken>()))
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
