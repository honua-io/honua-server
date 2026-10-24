// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// #6107: the result/status read path replays terminal callbacks to heal jobs whose first
/// transition never ran them. That replay must be non-destructive.
/// </summary>
public sealed partial class GeoprocessingJobServiceTests
{
    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task GetJobResultsForTerminal_ReplayPackageStoreFailure_KeepsSucceededAndIsRetryable()
    {
        using var harness = CreateTerminalReplayHarness();
        harness.Packages.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AnalysisResultPackage?)null);
        harness.Packages.SetAsync(
                Arg.Any<string>(),
                Arg.Any<AnalysisResultPackage>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("result-package store timed out"));

        var read = () => harness.Service.GetJobResultsForTerminalAsync(
            harness.Record.OperationId, CreatePrincipal());

        await read.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
        harness.Current.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await harness.Jobs.DidNotReceiveWithAnyArgs().TrySetAsync(default!, default, default);
        harness.Observer.Seen.Should().NotContain(job => job.Status == ExecutionJobStatus.Failed);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetJobResultsForTerminal_StoredPackage_DoesNotReplayOrRepersist()
    {
        using var harness = CreateTerminalReplayHarness();
        var package = AnalysisResultPackage.CreateCompleted(
            GeoprocessingResultPackageFactory.CreateResultPackageId(harness.Record),
            new ResultSummary { Title = "Stored result" },
            [],
            [],
            new ProvenanceRecord { Sources = [], ProcessDefinitions = ["geometry.buffer"] });
        harness.Packages.GetAsync(harness.Record.OperationId, Arg.Any<CancellationToken>())
            .Returns(package);

        var result = await harness.Service.GetJobResultsForTerminalAsync(
            harness.Record.OperationId, CreatePrincipal());

        result.ResultPackageId.Should().Be(package.ResultPackageId);
        await harness.Packages.DidNotReceiveWithAnyArgs().SetAsync(default!, default!, default, default);
        harness.Observer.Seen.Should().BeEmpty();
        harness.Current.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    private TerminalReplayHarness CreateTerminalReplayHarness()
    {
        var record = CreateOwnedJobRecord("job-replay-6107", ExecutionJobStatus.Succeeded, owner: "test-user") with
        {
            CompletedAt = DateTimeOffset.UtcNow
        };
        var current = record;
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(record.OperationId, Arg.Any<CancellationToken>()).Returns(_ => current);
        jobs.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                current = call.Arg<ExecutionJobRecord>();
                return true;
            });

        var packages = Substitute.For<IGeoprocessingResultPackageStore>();
        // The observer stands in for the other registered terminal callbacks (Ops alerts,
        // share export): it sees both the replay and any corrected-Failed re-notification.
        var observer = new RecordingTerminalCallback();
        var services = new ServiceCollection();
        services.AddSingleton<IJobTerminalCallback>(observer);
        var provider = services.BuildServiceProvider();

        var progress = Substitute.For<IUniversalProgressStore>();
        progress.GetProgressAsync<GeoprocessingProgress>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GeoprocessingProgress?)null);
        var catalog = new BuiltInProcessCatalog();
        var gpCallback = new GeoprocessingJobTerminalCallback(
            progress,
            catalog,
            DefaultExecutorOptions,
            packages,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GeoprocessingJobTerminalCallback>.Instance,
            executionJobStore: jobs);

        var service = new GeoprocessingJobService(
            progress, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            catalog,
            NullLogger<GeoprocessingJobService>.Instance,
            DefaultExecutorOptions,
            jobs,
            resultPackageStore: packages,
            terminalCallbacks: [gpCallback, observer]);

        return new TerminalReplayHarness
        {
            Record = record,
            Jobs = jobs,
            Packages = packages,
            Observer = observer,
            Service = service,
            Provider = provider,
            CurrentAccessor = () => current,
        };
    }

    private sealed class TerminalReplayHarness : IDisposable
    {
        public required ExecutionJobRecord Record { get; init; }
        public required IExecutionJobStore Jobs { get; init; }
        public required IGeoprocessingResultPackageStore Packages { get; init; }
        public required RecordingTerminalCallback Observer { get; init; }
        public required GeoprocessingJobService Service { get; init; }
        public required Func<ExecutionJobRecord> CurrentAccessor { get; init; }
        public ExecutionJobRecord Current => CurrentAccessor();

        public required ServiceProvider Provider { get; init; }

        public void Dispose() => Provider.Dispose();
    }

    private sealed class RecordingTerminalCallback : IJobTerminalCallback
    {
        private readonly List<ExecutionJobRecord> _seen = [];

        public IReadOnlyList<ExecutionJobRecord> Seen => _seen;

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            _seen.Add(job);
            return ValueTask.CompletedTask;
        }
    }
}
