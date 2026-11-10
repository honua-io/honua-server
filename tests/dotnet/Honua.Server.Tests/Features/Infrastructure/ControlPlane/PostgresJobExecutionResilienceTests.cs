// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

/// <summary>
/// Job execution across a PostgreSQL outage. Each test owns its container, because it stops and
/// starts it, which the process-wide shared container cannot survive.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobExecutionResilienceTests
{
    private const string MigrationResourceName = "Honua.Server.Migrations.147_CreateJobSubstrate.sql";

    [IntegrationTest]
    public async Task JobExecutionService_WhenPostgresRestartsMidJob_JobCompletesExactlyOnce()
    {
        await using var substrate = await RestartableSubstrate.StartAsync();
        var operationId = $"job-postgres-restart-{Guid.NewGuid():N}";
        var callback = new RecordingTerminalCallback();
        var executor = new CountingDelayedExecutor(TimeSpan.FromSeconds(15));
        using var service = substrate.CreateWorker(executor, callback);
        await substrate.JobStore.TryCreateAsync(CreateQueuedJob(operationId, JobHeartbeatPolicy.Default, maxAttempts: 5));
        await substrate.Queue.EnqueueAsync(operationId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await executor.Started.WaitAsync(TimeSpan.FromSeconds(30));

            await substrate.RestartAsync(downFor: TimeSpan.FromSeconds(3));

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
            await service.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task JobReconciliation_AfterAnOutageLongerThanTheHeartbeatTimeout_DoesNotExpireALiveWorkersJob()
    {
        await using var substrate = await RestartableSubstrate.StartAsync();
        var operationId = $"job-long-outage-{Guid.NewGuid():N}";
        var heartbeat = new JobHeartbeatPolicy { Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromSeconds(4) };
        var callback = new RecordingTerminalCallback();
        var executor = new CountingDelayedExecutor(TimeSpan.FromSeconds(30));
        using var worker = substrate.CreateWorker(executor, callback);
        using var reconciler = new JobReconciliationService(
            substrate.JobStore,
            substrate.Queue,
            substrate.Queue,
            new ExecutionJobCancellationTokens(),
            [callback],
            substrate.LogStore,
            NullLogger<JobReconciliationService>.Instance,
            substrate.JobStore,
            substrate.JobStore);
        // One attempt only: an expiry would fail the job outright instead of hiding behind a retry.
        await substrate.JobStore.TryCreateAsync(CreateQueuedJob(operationId, heartbeat, maxAttempts: 1));
        await substrate.Queue.EnqueueAsync(operationId);

        using var sweeping = new CancellationTokenSource();
        var failedSweeps = 0;
        var sweeps = Task.Run(async () =>
        {
            while (!sweeping.IsCancellationRequested)
            {
                try
                {
                    await reconciler.SweepActiveJobsAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
                {
                    Interlocked.Increment(ref failedSweeps);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executor.Started.WaitAsync(TimeSpan.FromSeconds(30));

            await substrate.RestartAsync(downFor: heartbeat.Timeout * 2);

            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(120));
            failedSweeps.Should().BePositive("the reconciler must have swept during the outage for this proof to mean anything");
            terminal.Status.Should().Be(
                ExecutionJobStatus.Succeeded,
                "the worker was alive throughout; only the database was away");
            terminal.AttemptCount.Should().Be(1);
            executor.Executions.Should().Be(1);
        }
        finally
        {
            await sweeping.CancelAsync();
            await sweeps;
            await worker.StopAsync(CancellationToken.None);
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

    private sealed class RestartableSubstrate : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _container;
        private readonly NpgsqlDataSource _dataSource;

        private RestartableSubstrate(PostgreSqlContainer container, NpgsqlDataSource dataSource)
        {
            _container = container;
            _dataSource = dataSource;
            var database = new JobStoreDatabase(dataSource);
            var options = Options.Create(new JobStoreOptions());
            JobStore = new PostgresExecutionJobStore(database, options, NullLogger<PostgresExecutionJobStore>.Instance);
            Queue = new PostgresJobQueue(database, NullLogger<PostgresJobQueue>.Instance);
            LogStore = new PostgresExecutionLogStore(database, options, NullLogger<PostgresExecutionLogStore>.Instance);
        }

        public PostgresExecutionJobStore JobStore { get; }

        public PostgresJobQueue Queue { get; }

        public PostgresExecutionLogStore LogStore { get; }

        public static async Task<RestartableSubstrate> StartAsync()
        {
            // A fixed host port: the mapping of a dynamically published port can change across
            // a stop and start, which would leave the pool pointing at a dead port.
            var container = new PostgreSqlBuilder()
                .WithImage("postgis/postgis:18-3.6")
                .WithPortBinding(ReserveLoopbackPort(), 5432)
                .Build();
            await container.StartAsync();
            var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
            await using (var migrate = dataSource.CreateCommand(
                             $"CREATE SCHEMA {JobStoreDatabase.DefaultSchemaName}; "
                             + ReadMigration().Replace("$HonuaSchema$", JobStoreDatabase.DefaultSchemaName, StringComparison.Ordinal)))
            {
                await migrate.ExecuteNonQueryAsync();
            }

            return new RestartableSubstrate(container, dataSource);
        }

        public JobExecutionService CreateWorker(IJobExecutor executor, IJobTerminalCallback callback)
            => new(Queue, JobStore, [executor], new ExecutionJobCancellationTokens(), [callback], LogStore,
                NullLogger<JobExecutionService>.Instance);

        public async Task RestartAsync(TimeSpan downFor)
        {
            await _container.StopAsync();
            await Task.Delay(downFor);
            await _container.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _container.DisposeAsync();
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

    private sealed class CountingDelayedExecutor(TimeSpan delay) : IJobExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executions;

        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public Task Started => _started.Task;

        public int Executions => Volatile.Read(ref _executions);

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            _started.TrySetResult();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
