// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Job execution across a PostgreSQL outage. This class owns one restartable container so it can
// stop and start Postgres without disturbing the process-wide shared container.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobExecutionResilienceTests(PostgresJobExecutionResilienceTests.RestartableSubstrate substrate)
    : IClassFixture<PostgresJobExecutionResilienceTests.RestartableSubstrate>
{
    private const string MigrationResourceName = "Honua.Server.Migrations.147_CreateJobSubstrate.sql";

    [IntegrationTest]
    public async Task JobExecutionService_WhenPostgresRestartsMidJob_JobCompletesExactlyOnce()
    {
        var operationId = $"job-postgres-restart-{Guid.NewGuid():N}";
        var callback = new RecordingTerminalCallback();
        var executor = new GatedExecutor();
        using var service = substrate.CreateWorker(executor, callback);
        await substrate.JobStore.TryCreateAsync(CreateQueuedJob(operationId, JobHeartbeatPolicy.Default, maxAttempts: 5));
        await substrate.Queue.EnqueueAsync(operationId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await executor.Started.WaitAsync(TimeSpan.FromSeconds(30));

            await substrate.RestartAsync(downFor: TimeSpan.FromSeconds(3));
            executor.Release();

            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(90));
            terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);
            var stored = (await substrate.JobStore.GetAsync(operationId))!;
            stored.Status.Should().Be(ExecutionJobStatus.Succeeded);
            stored.AttemptCount.Should().Be(1, "the restart must not cost the job an attempt");
            stored.CompletedAt.Should().NotBeNull();
            executor.Executions.Should().Be(1, "the job ran while the database was away and must not run again");
            (await substrate.Queue.GetQueueDepthAsync()).Should().Be(0);
        }
        finally
        {
            executor.Release();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task JobReconciliation_AfterAnOutageLongerThanTheHeartbeatTimeout_DoesNotExpireALiveWorkersJob()
    {
        var operationId = $"job-long-outage-{Guid.NewGuid():N}";
        var heartbeat = new JobHeartbeatPolicy { Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromSeconds(4) };
        var callback = new RecordingTerminalCallback();
        var executor = new GatedExecutor();
        using var worker = substrate.CreateWorker(executor, callback);
        using var reconciler = substrate.CreateReconciler(callback);
        // One attempt only: an expiry would fail the job outright instead of hiding behind a retry.
        await substrate.JobStore.TryCreateAsync(CreateQueuedJob(operationId, heartbeat, maxAttempts: 1));
        await substrate.Queue.EnqueueAsync(operationId);

        await using var sweepLoop = new OutageSweepLoop(reconciler, heartbeat.Timeout);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.Started.WaitAsync(TimeSpan.FromSeconds(30));

            await substrate.RestartAsync(downFor: heartbeat.Timeout * 2);
            sweepLoop.MarkRecovered();
            await sweepLoop.PostGraceSweepCompleted.WaitAsync(TimeSpan.FromSeconds(30));
            executor.Release();

            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(120));
            sweepLoop.FailedSweeps.Should().BePositive("the reconciler must have swept during the outage for this proof to mean anything");
            terminal.Status.Should().Be(
                ExecutionJobStatus.Succeeded,
                "the worker was alive throughout; only the database was away");
            terminal.AttemptCount.Should().Be(1);
            executor.Executions.Should().Be(1);
        }
        finally
        {
            executor.Release();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class OutageSweepLoop : IAsyncDisposable
    {
        private readonly JobReconciliationService _reconciler;
        private readonly TimeSpan _gracePeriod;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _postGraceSweepCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private int _failedSweeps;
        private long _recoveredAt;

        public OutageSweepLoop(JobReconciliationService reconciler, TimeSpan gracePeriod)
        {
            _reconciler = reconciler;
            _gracePeriod = gracePeriod;
            _loop = RunAsync();
        }

        public int FailedSweeps => Volatile.Read(ref _failedSweeps);

        public Task PostGraceSweepCompleted => _postGraceSweepCompleted.Task;

        public void MarkRecovered() => Volatile.Write(ref _recoveredAt, Stopwatch.GetTimestamp());

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _loop;
            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                await SweepOnceAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        private async Task SweepOnceAsync()
        {
            try
            {
                await _reconciler.SweepActiveJobsAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                Interlocked.Increment(ref _failedSweeps);
                return;
            }

            var recoveredTimestamp = Volatile.Read(ref _recoveredAt);
            if (recoveredTimestamp != 0 && Stopwatch.GetElapsedTime(recoveredTimestamp) >= _gracePeriod)
            {
                _postGraceSweepCompleted.TrySetResult();
            }
        }
    }

    private static ExecutionJobRecord CreateQueuedJob(string operationId, JobHeartbeatPolicy heartbeatPolicy, int maxAttempts)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            HeartbeatPolicy = heartbeatPolicy,
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = maxAttempts,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            },
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromMinutes(5) },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "postgres-restart-resilience"
            }
        };
    }

    public sealed class RestartableSubstrate : IAsyncLifetime
    {
        private PostgreSqlContainer? _container;
        private NpgsqlDataSource? _dataSource;

        internal PostgresExecutionJobStore JobStore { get; private set; } = null!;

        internal PostgresJobQueue Queue { get; private set; } = null!;

        internal PostgresExecutionLogStore LogStore { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            // A fixed host port: the mapping of a dynamically published port can change across
            // a stop and start, which would leave the pool pointing at a dead port.
            _container = new PostgreSqlBuilder()
                .WithImage("postgis/postgis:18-3.6")
                .WithPortBinding(ReserveLoopbackPort(), 5432)
                .Build();
            await _container.StartAsync();
            _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
            await using (var migrate = _dataSource.CreateCommand(
                             $"CREATE SCHEMA {JobStoreDatabase.DefaultSchemaName}; "
                             + ReadMigration().Replace("$HonuaSchema$", JobStoreDatabase.DefaultSchemaName, StringComparison.Ordinal)))
            {
                await migrate.ExecuteNonQueryAsync();
            }

            var database = new JobStoreDatabase(_dataSource);
            var options = Options.Create(new JobStoreOptions());
            JobStore = new PostgresExecutionJobStore(database, options, NullLogger<PostgresExecutionJobStore>.Instance);
            Queue = new PostgresJobQueue(database, NullLogger<PostgresJobQueue>.Instance);
            LogStore = new PostgresExecutionLogStore(database, options, NullLogger<PostgresExecutionLogStore>.Instance);
        }

        internal JobExecutionService CreateWorker(IJobExecutor executor, IJobTerminalCallback callback)
            => new(Queue, JobStore, [executor], new ExecutionJobCancellationTokens(), [callback], LogStore,
                NullLogger<JobExecutionService>.Instance);

        internal JobReconciliationService CreateReconciler(IJobTerminalCallback callback)
            => new(
                JobStore,
                Queue,
                Queue,
                new ExecutionJobCancellationTokens(),
                [callback],
                LogStore,
                NullLogger<JobReconciliationService>.Instance,
                JobStore,
                JobStore);

        internal async Task RestartAsync(TimeSpan downFor)
        {
            await _container!.StopAsync();
            await Task.Delay(downFor);
            await _container.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (_dataSource is not null)
            {
                await _dataSource.DisposeAsync();
            }

            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }

        private static string ReadMigration()
        {
            using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream(MigrationResourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{MigrationResourceName}' was not found.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static int ReserveLoopbackPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private sealed class GatedExecutor : IJobExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executions;

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public Task Started => _started.Task;

        public int Executions => Volatile.Read(ref _executions);

        public void Release() => _release.TrySetResult();

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
    }

    private sealed class RecordingTerminalCallback : IJobTerminalCallback
    {
        private readonly TaskCompletionSource<ExecutionJobRecord> _whenCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionJobRecord> WhenCompleted => _whenCompleted.Task;

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            _whenCompleted.TrySetResult(job);
            return ValueTask.CompletedTask;
        }
    }
}
