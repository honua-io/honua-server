// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport.Durable;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Import)]
public sealed class BoundImportAcceptanceTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task RetryWithNewTrace_RetainsOriginalCorrelationAndRejectsExplicitLineageChange()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        using var harness = new Harness(store);
        var request = Request();
        string id;
        using (var originalTrace = new System.Diagnostics.Activity("original")
            .AddBaggage("correlation.id", "original-correlation").Start())
            id = await harness.Service.QueueImportAsync(request, 100);
        using var retryTrace = new System.Diagnostics.Activity("retry")
            .AddBaggage("correlation.id", "retry-correlation").Start();

        (await harness.Service.QueueImportAsync(request, 100)).Should().Be(id);
        var accepted = (await store.GetAsync(id))!;
        accepted.Audit.CorrelationId.Should().Be("original-correlation");
        accepted.Spec.Parameters[FileImportJobParameters.CorrelationId].Should().Be("original-correlation");
        await FluentActions.Invoking(() => harness.Service.QueueImportAsync(
            request with { CorrelationId = "changed-explicit-lineage" }, 100))
            .Should().ThrowAsync<ImportAcceptanceConflictException>();
        await harness.Queue.Received(1).EnqueueAsync(id, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    public async Task ConcurrentCompletion_CreatesOneDurableJobAndDispatchesOnlyTheWinner()
    {
        await using var a = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        await using var b = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var firstStore = Store(a);
        var secondStore = Store(b);
        using var first = new Harness(firstStore);
        using var second = new Harness(secondStore);
        var request = Request();

        var accepted = await Task.WhenAll(first.Service.QueueImportAsync(request, 100), second.Service.QueueImportAsync(request, 100));

        accepted.Should().OnlyContain(id => id == request.Acceptance!.OperationId);
        var calls = first.Queue.ReceivedCalls().Concat(second.Queue.ReceivedCalls())
            .Count(call => call.GetMethodInfo().Name == nameof(IJobQueue.EnqueueAsync));
        calls.Should().Be(1);
        var job = (await secondStore.GetAsync(accepted[0]))!;
        job.Version.Should().Be(1);
        FileImportJobParameters.TryParse(job.Spec.Parameters, out var restored, out var bytes, out _).Should().BeTrue();
        bytes.Should().Be(100);
        restored!.Acceptance.Should().Be(request.Acceptance);
        restored.Ownership.Should().Be(request.Ownership);
        // Replaying a terminal original also returns its identity without a new attempt.
        (await firstStore.TrySetAsync(job with { Status = ExecutionJobStatus.Failed, ErrorMessage = "original failure" }))
            .Should().BeTrue();
        (await second.Service.QueueImportAsync(request, 100)).Should().Be(accepted[0]);
        (await secondStore.GetAsync(accepted[0]))!.Status.Should().Be(ExecutionJobStatus.Failed);
        first.Queue.ReceivedCalls().Concat(second.Queue.ReceivedCalls())
            .Count(call => call.GetMethodInfo().Name == nameof(IJobQueue.EnqueueAsync)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task LostCreateReply_RestartRecoversOriginalWithoutDispatch_AndSweepFindsCommittedIntent()
    {
        await using var originalConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var real = Store(originalConnection);
        var uncertain = Substitute.For<IExecutionJobStore>();
        uncertain.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        uncertain.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => real.ListActiveAsync(call.ArgAt<ExecutionJobKind?>(0), call.ArgAt<int?>(1), call.ArgAt<CancellationToken>(2)));
        uncertain.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(async call =>
            {
                (await real.TryCreateAsync(call.ArgAt<ExecutionJobRecord>(0), call.ArgAt<TimeSpan?>(1), call.ArgAt<CancellationToken>(2)))
                    .Should().BeTrue();
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "Reply lost after actual commit");
            });
        using var original = new Harness(uncertain);
        var request = Request();
        var id = request.Acceptance!.OperationId;

        await FluentActions.Invoking(() => original.Service.QueueImportAsync(request, 100)).Should().ThrowAsync<RedisConnectionException>();
        original.Queue.ReceivedCalls().Should().BeEmpty();

        await using var replacementConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var recoveredStore = Store(replacementConnection);
        using var recovered = new Harness(recoveredStore);
        (await recovered.Service.GetProgressAsync(id))!.JobId.Should().Be(id);
        (await recovered.Service.QueueImportAsync(request, 100)).Should().Be(id);
        recovered.Queue.ReceivedCalls().Should().BeEmpty("observation and replay must not dispatch");
        var job = (await recoveredStore.GetAsync(id))!;
        job.Version.Should().Be(1);
        (await recoveredStore.ListActiveAsync(ExecutionJobKind.FileImport, 200)).Should().Contain(record => record.OperationId == id);

        var queue = new RedisJobQueue(replacementConnection, recoveredStore, NullLogger<RedisJobQueue>.Instance);
        // Limit this sweep to the test's actual indexed record, avoiding mutation
        // of unrelated jobs retained by other tests in the shared Redis fixture.
        var repairView = Substitute.For<IExecutionJobStore>();
        repairView.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(async call => (IReadOnlyList<ExecutionJobRecord>)(await recoveredStore.ListActiveAsync(
                call.ArgAt<ExecutionJobKind?>(0), call.ArgAt<int?>(1), call.ArgAt<CancellationToken>(2)))
                .Where(record => record.OperationId == id).ToArray());
        repairView.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(call => recoveredStore.GetAsync(id, call.ArgAt<CancellationToken>(1)));
        using var sweep = new JobReconciliationService(repairView, queue, queue,
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobReconciliationService>.Instance);
        await sweep.SweepActiveJobsAsync(CancellationToken.None);
        var score = await replacementConnection.GetDatabase().SortedSetScoreAsync("controlplane:jobqueue:pending", id);
        score.Should().NotBeNull();
        await sweep.SweepActiveJobsAsync(CancellationToken.None);
        (await replacementConnection.GetDatabase().SortedSetScoreAsync("controlplane:jobqueue:pending", id)).Should().Be(score);
        (await recoveredStore.GetAsync(id))!.Version.Should().Be(1);
    }

    [IntegrationTest]
    public async Task SameIdentityWithChangedOwnerIntentSourceDestinationOrLength_ConflictsWithoutDispatch()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        using var harness = new Harness(store);
        var request = Request();
        var id = await harness.Service.QueueImportAsync(request, 100);
        var original = await store.GetAsync(id);
        ImportRequest[] changed =
        [
            request with { Ownership = request.Ownership! with { OwnerActorId = "foreign" } },
            request with { Ownership = request.Ownership! with { TenantId = "foreign" } },
            request with { Acceptance = request.Acceptance! with { IntentSha256 = new string('b', 64) } },
            request with { CloudFileId = "another-source" },
            request with { TableName = "another_table" },
            request with { TargetSchema = "another_schema" },
            request with { TargetSrid = 3857 },
            request with { OverwriteExisting = true }
        ];
        foreach (var mutation in changed)
        {
            await FluentActions.Invoking(() => harness.Service.QueueImportAsync(mutation, 100))
                .Should().ThrowAsync<ImportAcceptanceConflictException>();
        }

        await FluentActions.Invoking(() => harness.Service.QueueImportAsync(request, 101))
            .Should().ThrowAsync<ImportAcceptanceConflictException>();
        (await store.GetAsync(id)).Should().BeEquivalentTo(original);
        await harness.Queue.Received(1).EnqueueAsync(id, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    public async Task ExpiredUnknownIntent_CannotRecreateRetiredJob()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        using var harness = new Harness(store);
        var request = Request();
        request = request with { Acceptance = request.Acceptance! with { AcceptBefore = DateTimeOffset.UtcNow.AddMinutes(-1) } };

        await FluentActions.Invoking(() => harness.Service.QueueImportAsync(request, 100)).Should().ThrowAsync<InvalidOperationException>();
        (await store.GetAsync(request.Acceptance!.OperationId)).Should().BeNull();
        harness.Queue.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task DeadlinePassesInsideCreate_StoreRefusesJobAndDispatchAfterHostAdmission()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var real = Store(connection);
        var delayed = Substitute.For<IExecutionJobStore>();
        delayed.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        delayed.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => real.ListActiveAsync(call.ArgAt<ExecutionJobKind?>(0), call.ArgAt<int?>(1), call.ArgAt<CancellationToken>(2)));
        var enteredCreate = false;
        delayed.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                enteredCreate = true;
                var job = call.ArgAt<ExecutionJobRecord>(0);
                job.AcceptanceDeadline.Should().NotBeNull();
                var remaining = job.AcceptanceDeadline!.Value - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                await Task.Delay(50);
                // Actual Redis Lua must enforce the deadline; a prior host check cannot.
                return await real.TryCreateAsync(job, cancellationToken: call.ArgAt<CancellationToken>(2));
            });
        using var harness = new Harness(delayed);
        var request = Request();
        request = request with { Acceptance = request.Acceptance! with { AcceptBefore = DateTimeOffset.UtcNow.AddSeconds(2) } };

        await FluentActions.Invoking(() => harness.Service.QueueImportAsync(request, 100))
            .Should().ThrowAsync<InvalidOperationException>();

        enteredCreate.Should().BeTrue("the deadline must be crossed after the host admission check");
        var id = request.Acceptance!.OperationId;
        (await real.GetAsync(id)).Should().BeNull();
        (await connection.GetDatabase().SetContainsAsync("controlplane:job:active", id)).Should().BeFalse();
        (await connection.GetDatabase().SetContainsAsync("controlplane:job:active:fileimport", id)).Should().BeFalse();
        (await connection.GetDatabase().SortedSetScoreAsync("controlplane:job:index:created:all", id)).Should().BeNull();
        harness.Queue.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task AcceptedBeforeDeadline_ReplayAfterExpiryRecoversOriginalWithoutDispatch()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        var request = Request();
        request = request with { Acceptance = request.Acceptance! with { AcceptBefore = DateTimeOffset.UtcNow.AddSeconds(2) } };
        var acceptance = request.Acceptance!;
        var job = DurableImportJobService.BuildJobRecord(acceptance.OperationId, request, 100,
            BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false), DateTimeOffset.UtcNow);
        (await store.TryCreateAsync(job)).Should().BeTrue();
        var remaining = acceptance.AcceptBefore - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        await Task.Delay(50);
        await using var restarted = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var recoveredStore = Store(restarted);
        using var recovered = new Harness(recoveredStore);

        (await recovered.Service.QueueImportAsync(request, 100)).Should().Be(job.OperationId);

        var original = (await recoveredStore.GetAsync(job.OperationId))!;
        original.AcceptanceDeadline.Should().Be(acceptance.AcceptBefore);
        original.Version.Should().Be(1);
        recovered.Queue.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task BoundAcceptance_RequiresDurableRunnerAndCannotFallBackToInProcess()
    {
        using var harness = new Harness(Substitute.For<IExecutionJobStore>(), durable: false);
        await FluentActions.Invoking(() => harness.Service.QueueImportAsync(Request(), 100)).Should().ThrowAsync<InvalidOperationException>();
        harness.InProcess.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public void AcceptanceParameters_PartialBindingIsRejected()
    {
        var request = Request();
        var parameters = FileImportJobParameters.Build(request, 100);
        parameters.Remove(FileImportJobParameters.AcceptanceDeadline);
        FileImportJobParameters.TryParse(parameters, out _, out _, out _).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task AcceptedStagedGeneration_CannotBeChangedOrDowngradedOnReplay()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var store = Store(connection);
        using var harness = new Harness(store);
        var request = Request();
        request = request with { StagedSource = new(request.CloudFileId!, "generation-1", 100, new string('a', 64)) };
        var id = await harness.Service.QueueImportAsync(request, 100);
        var original = (await store.GetAsync(id))!;
        FileImportJobParameters.TryParse(original.Spec.Parameters, out var restored, out _, out _).Should().BeTrue();
        restored!.StagedSource.Should().Be(request.StagedSource);
        (await harness.Service.QueueImportAsync(request, 100)).Should().Be(id);
        ImportRequest[] changed =
        [
            request with { StagedSource = null },
            request with { StagedSource = request.StagedSource! with { ETag = "generation-2" } },
            request with { StagedSource = request.StagedSource! with { Sha256 = new string('b', 64) } }
        ];
        foreach (var mutation in changed)
        {
            await FluentActions.Invoking(() => harness.Service.QueueImportAsync(mutation, 100))
                .Should().ThrowAsync<ImportAcceptanceConflictException>();
        }
        (await store.GetAsync(id)).Should().BeEquivalentTo(original);
        await harness.Queue.Received(1).EnqueueAsync(id, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task StagedSource_WithoutAcceptanceStillCannotFallBackToUnverifiedInProcessRunner()
    {
        using var harness = new Harness(Substitute.For<IExecutionJobStore>(), durable: false);
        var request = Request();
        request = request with { Acceptance = null, StagedSource = new(request.CloudFileId!, "generation", 100, new string('a', 64)) };
        await FluentActions.Invoking(() => harness.Service.QueueImportAsync(request, 100))
            .Should().ThrowAsync<InvalidOperationException>();
        harness.InProcess.ReceivedCalls().Should().BeEmpty();
    }

    private static RedisExecutionJobStore Store(IConnectionMultiplexer connection) =>
        new(connection, NullLogger<RedisExecutionJobStore>.Instance);

    private static ImportRequest Request() => new()
    {
        Acceptance = new ImportAcceptanceBinding(Guid.NewGuid().ToString("N"), new string('a', 64), DateTimeOffset.UtcNow.AddHours(1)),
        Ownership = new ImportOperationOwnership("owner", "tenant"),
        CloudFileId = "imports/server-generated-verified-source",
        FileName = "data.geojson",
        TableName = "accepted_data",
        TargetSchema = "honua_data"
    };

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        public IJobQueue Queue { get; } = Substitute.For<IJobQueue>();
        public IImportJobService InProcess { get; } = Substitute.For<IImportJobService>();
        public DurableImportJobService Service { get; }

        public Harness(IExecutionJobStore store, bool durable = true)
        {
            var importer = Substitute.For<IFileImportService>();
            importer.DetectFormat(Arg.Any<string>()).Returns(SupportedFileFormat.GeoJson);
            // These tests isolate durable acceptance, not concurrent-capacity admission.
            importer.Limits.Returns(new ImportLimits { MaxConcurrentBackgroundJobs = 10000 });
            _services = new ServiceCollection().AddSingleton(importer).BuildServiceProvider();
            Service = new DurableImportJobService(InProcess,
                BackgroundImportRunnerDecision.Decide(durable, null, frozenBetweenRequests: false),
                Substitute.For<IUniversalProgressStore>(), _services.GetRequiredService<IServiceScopeFactory>(), [],
                NullLogger<DurableImportJobService>.Instance, store, Queue);
        }

        public void Dispose()
        {
            Service.Dispose();
            _services.Dispose();
        }
    }
}
