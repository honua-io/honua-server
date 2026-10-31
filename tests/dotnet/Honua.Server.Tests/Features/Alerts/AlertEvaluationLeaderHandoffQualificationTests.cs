// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Alerts;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Two production evaluator replicas against Postgres: advisory-lock handoff, a database
/// restart while numbered generations are in flight, and replica restarts that keep the
/// checkpoint and one event per generation (#3863).
/// </summary>
[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.ContractTesting)]
public sealed class AlertEvaluationLeaderHandoffQualificationTests
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LeaderLease = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NoLeaderThreshold = TimeSpan.FromMilliseconds(400);
    private const string WorkerName = "evaluator";

    [IntegrationTest(Timeout = 180000)]
    public async Task TwoReplicas_HandoffRestartAndRecovery_KeepOneEventPerGeneration()
    {
        var timeline = new List<string>();
        var checkpointSamples = new List<long>();
        var attempts = new ConcurrentBag<EvaluationAttempt>();
        // Docker can remap an ephemeral host port across stop/start. Pin the port so the
        // replicas' Npgsql pools keep the endpoint they opened at startup.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var container = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:18-3.6")
            .WithDatabase("honua_leader_handoff")
            .WithUsername("honua")
            .WithPassword("leader_handoff_password")
            .WithPortBinding(hostPort, 5432)
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable("HONUA_TEST_RUN_ID") ?? "manual")
            .Build();
        await container.StartAsync();
        container.GetMappedPublicPort(5432).Should().Be((ushort)hostPort);

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Timeout = 2,
            CommandTimeout = 5,
            Multiplexing = false,
            PersistSecurityInfo = true,
            MaxPoolSize = 20,
            KeepAlive = 0,
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await InitializeSchemaAsync(dataSource);

        var options = new AlertOptions
        {
            Enabled = true,
            Evaluation = new AlertEvaluationOptions
            {
                WorkerName = WorkerName,
                IdleDelay = IdleDelay,
                LeaderLeaseDuration = LeaderLease,
                DwellSweepInterval = TimeSpan.FromHours(1),
                ChangeBatchSize = 20,
                NoLeaderThreshold = NoLeaderThreshold,
                HeartbeatStalenessThreshold = TimeSpan.FromSeconds(30),
            },
        };
        var store = new PostgresAlertCheckpointStore(new TestConnectionProvider(dataSource));
        var replicas = new List<Replica>
        {
            CreateReplica("replica-a", dataSource, options, store, attempts),
            CreateReplica("replica-b", dataSource, options, store, attempts),
        };

        try
        {
            foreach (var replica in replicas)
            {
                await replica.Service.StartAsync(CancellationToken.None);
            }

            await WaitUntilAsync(() => replicas.Count(static replica => replica.Service.IsLeader) == 1);
            Note(timeline, "startup " + Describe(replicas));
            (await SampleCheckpointAsync(dataSource, checkpointSamples)).Should().Be(0);

            await PublishAsync(dataSource, 1);
            await WaitUntilAsync(() => attempts.Any(static attempt => attempt.Generation == 1 && attempt.Wrote));
            await WaitUntilAsync(async () => await SampleCheckpointAsync(dataSource, checkpointSamples) == 1);
            var firstLeader = replicas.Single(static replica => replica.Service.IsLeader);
            var firstFollower = replicas.Single(static replica => !replica.Service.IsLeader);
            attempts.Where(static attempt => attempt.Generation == 1).Select(static attempt => attempt.Replica)
                .Distinct()
                .Should()
                .Equal(firstLeader.Name);
            Note(timeline, $"generation 1 evaluated by {firstLeader.Name}; follower {firstFollower.Name}");

            await firstLeader.Gate.PauseAsync();
            DateTimeOffset lostAt;
            try
            {
                var leaderPid = await ReadLockPidAsync(dataSource);
                leaderPid.Should().BeGreaterThan(0);
                lostAt = DateTimeOffset.UtcNow;
                await TerminateBackendAsync(dataSource, leaderPid);
                Note(timeline, $"severed advisory-lock backend {leaderPid} of {firstLeader.Name}");
                await PublishAsync(dataSource, 2);
                await WaitUntilAsync(() => attempts.Any(attempt =>
                    attempt.Generation == 2 && attempt.Wrote && attempt.Replica == firstFollower.Name));
            }
            finally
            {
                firstLeader.Gate.Resume();
            }
            var handoffAt = attempts.Where(attempt => attempt.Generation == 2 && attempt.Wrote).Min(static attempt => attempt.At);
            var handoffBudget = LeaderLease + IdleDelay + IdleDelay;
            handoffAt.Should().BeOnOrBefore(lostAt + handoffBudget);
            attempts.Where(static attempt => attempt.Generation == 2).Select(static attempt => attempt.Replica)
                .Distinct()
                .Should()
                .Equal(firstFollower.Name);
            await WaitUntilAsync(() => replicas.Count(static replica => replica.Service.IsLeader) == 1
                && firstFollower.Service.IsLeader
                && !firstLeader.Service.IsLeader);
            Note(timeline, "handoff " + Describe(replicas));
            HealthOf(firstFollower).Status.Should().Be(HealthStatus.Healthy);
            HealthOf(firstLeader).Status.Should().Be(HealthStatus.Healthy);

            await PublishAsync(dataSource, 3);
            await PublishAsync(dataSource, 4);
            await PublishAsync(dataSource, 5);
            await WaitUntilAsync(async () => await CountEventsAsync(dataSource) == 5
                && await SampleCheckpointAsync(dataSource, checkpointSamples) == 5);
            (await SampleCheckpointAsync(dataSource, checkpointSamples)).Should().Be(5);

            await container.StopAsync();
            Note(timeline, "postgres stopped");
            dataSource.Clear();
            var outageWriteFailed = false;
            try
            {
                await PublishAsync(dataSource, 6);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or SocketException or IOException)
            {
                outageWriteFailed = true;
                Note(timeline, $"generation 6 rejected while postgres is down ({ex.GetType().Name})");
            }

            outageWriteFailed.Should().BeTrue();
            await WaitUntilAsync(() => replicas.All(static replica =>
                replica.Service.IsEvaluatorRunning
                && HealthOf(replica).Status == HealthStatus.Unhealthy));
            Note(timeline, "no-leader " + Describe(replicas));
            replicas.Should().AllSatisfy(replica =>
                HealthOf(replica).Description.Should().Contain("no leader"));

            await container.StartAsync();
            container.GetMappedPublicPort(5432).Should().Be((ushort)hostPort);
            Note(timeline, $"postgres started on host port {hostPort}");
            await WaitUntilHostAcceptsAsync(dataSource.ConnectionString);
            dataSource.Clear();
            await PublishAfterRecoveryAsync(dataSource, 6);
            await PublishAfterRecoveryAsync(dataSource, 7);
            await PublishAfterRecoveryAsync(dataSource, 8);
            await WaitUntilAsync(async () => await CountEventsAsync(dataSource) == 8
                && await SampleCheckpointAsync(dataSource, checkpointSamples) == 8);
            await WaitUntilAsync(() => replicas.All(static replica => HealthOf(replica).Status == HealthStatus.Healthy)
                && replicas.Count(static replica => replica.Service.IsLeader) == 1);
            Note(timeline, "recovered " + Describe(replicas));
            (await SampleCheckpointAsync(dataSource, checkpointSamples)).Should().Be(8);

            var current = await store.GetAsync(WorkerName);
            await store.SetAsync(current with { LastGeneration = current.LastGeneration - 1 });
            (await store.GetAsync(WorkerName)).LastGeneration.Should().Be(current.LastGeneration);
            Note(timeline, $"stale checkpoint {current.LastGeneration - 1} left the cursor at {current.LastGeneration}");

            var firstName = replicas[0].Name;
            var secondName = replicas[1].Name;
            replicas = await ReplaceReplicaAsync(replicas, firstName, dataSource, options, store, attempts, timeline);
            await PublishAsync(dataSource, 9);
            await WaitUntilAsync(async () => await CountEventsAsync(dataSource) == 9
                && await SampleCheckpointAsync(dataSource, checkpointSamples) == 9);
            replicas = await ReplaceReplicaAsync(replicas, secondName, dataSource, options, store, attempts, timeline);
            await PublishAsync(dataSource, 10);
            await WaitUntilAsync(async () => await CountEventsAsync(dataSource) == 10
                && await SampleCheckpointAsync(dataSource, checkpointSamples) == 10);
            var finalCheckpoint = await SampleCheckpointAsync(dataSource, checkpointSamples);
            finalCheckpoint.Should().Be(10);

            var events = await ReadEventsAsync(dataSource);
            events.Select(static row => row.Generation).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
            events.Select(static row => row.Replica).Should().OnlyContain(name => name == "replica-a" || name == "replica-b");
            for (var generation = 1L; generation <= 10; generation++)
            {
                attempts.Where(attempt => attempt.Generation == generation)
                    .Select(static attempt => attempt.Replica)
                    .Distinct()
                    .Should()
                    .HaveCount(1, "generation {0} must be evaluated by one replica", generation);
            }

            checkpointSamples.Should().BeInAscendingOrder();
            var evidencePath = await WriteEvidenceAsync(timeline, events, finalCheckpoint);
            File.Exists(evidencePath).Should().BeTrue();
            var evidence = await File.ReadAllTextAsync(evidencePath);
            evidence.Should().Contain("packageDigest");
            evidence.Should().Contain("timeline");
        }
        finally
        {
            foreach (var replica in replicas)
            {
                await replica.DisposeAsync();
            }
        }
    }

    private static async Task<List<Replica>> ReplaceReplicaAsync(
        List<Replica> replicas,
        string name,
        NpgsqlDataSource dataSource,
        AlertOptions options,
        PostgresAlertCheckpointStore store,
        ConcurrentBag<EvaluationAttempt> attempts,
        List<string> timeline)
    {
        var index = replicas.FindIndex(replica => replica.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0);
        await replicas[index].DisposeAsync();
        Note(timeline, $"stopped {name}");
        var replacement = CreateReplica(name, dataSource, options, store, attempts);
        replicas[index] = replacement;
        await replacement.Service.StartAsync(CancellationToken.None);
        Note(timeline, $"restarted {name}");
        await WaitUntilAsync(() =>
            replicas.All(static replica => replica.Service.IsEvaluatorRunning)
            && replicas.Count(static replica => replica.Service.IsLeader) == 1
            && replicas.All(static replica => HealthOf(replica).Status == HealthStatus.Healthy));
        Note(timeline, "after restart " + Describe(replicas));
        return replicas;
    }

    private static async Task PublishAfterRecoveryAsync(NpgsqlDataSource dataSource, long generation)
    {
        Exception? last = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await PublishAsync(dataSource, generation);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // The outage insert can commit and still surface a connection failure.
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or SocketException or IOException)
            {
                last = ex;
                await DelayUntilAsync(deadline);
            }
        }

        throw new TimeoutException(
            $"generation {generation} was not accepted after Postgres restart ({last?.GetType().Name}: {last?.Message})",
            last);
    }

    private static async Task WaitUntilHostAcceptsAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Timeout = 2,
            CommandTimeout = 5,
        };
        await using var probe = NpgsqlDataSource.Create(builder.ConnectionString);
        Exception? last = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = await probe.OpenConnectionAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                _ = await command.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or SocketException or IOException)
            {
                last = ex;
                await DelayUntilAsync(deadline);
            }
        }

        throw new TimeoutException(
            $"Postgres at {builder.Host}:{builder.Port} did not accept a host connection after restart ({last?.GetType().Name}: {last?.Message})",
            last);
    }

    private static async Task DelayUntilAsync(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        var delay = remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
        await Task.Delay(delay);
    }

    private static Replica CreateReplica(
        string name,
        NpgsqlDataSource dataSource,
        AlertOptions options,
        IAlertCheckpointStore store,
        ConcurrentBag<EvaluationAttempt> attempts)
    {
        var optionValues = Options.Create(options);
        var strategy = new PostgresAdvisoryLockLeaderElectionStrategy(
            dataSource,
            NullLogger<PostgresAdvisoryLockLeaderElectionStrategy>.Instance,
            optionValues);
        var gate = new GatedLeaderStrategy(strategy);
        var pipeline = new NumberedGenerationPipeline(dataSource, name, attempts);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IAlertPipeline>(pipeline);
        var provider = services.BuildServiceProvider();
        var service = new AlertEvaluationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            gate,
            optionValues,
            TestTelemetry.CreateAlertPipelineMetrics(),
            NullLogger<AlertEvaluationBackgroundService>.Instance);
        var health = new AlertEvaluationHealthCheck(service, optionValues);
        return new Replica(name, service, strategy, gate, provider, health, pipeline);
    }

    private static HealthCheckResult HealthOf(Replica replica) => replica.Health.Evaluate(DateTimeOffset.UtcNow);

    private static string Describe(IEnumerable<Replica> replicas)
        => string.Join(
            "; ",
            replicas.Select(static replica =>
            {
                var health = HealthOf(replica);
                var role = replica.Service.IsLeader ? "leader" : "follower";
                return $"{replica.Name} {role} {health.Status}";
            }));

    private static void Note(List<string> timeline, string message)
        => timeline.Add($"{DateTimeOffset.UtcNow:O} {message}");

    private static async Task InitializeSchemaAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS honua;
            CREATE TABLE IF NOT EXISTS honua.alert_worker_checkpoint (
                worker_name TEXT PRIMARY KEY,
                last_generation BIGINT NOT NULL DEFAULT 0,
                last_dwell_sweep_at TIMESTAMPTZ,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                CONSTRAINT alert_worker_checkpoint_valid_worker CHECK (length(worker_name) > 0),
                CONSTRAINT alert_worker_checkpoint_valid_generation CHECK (last_generation >= 0)
            );
            CREATE TABLE IF NOT EXISTS honua.alert_eval_qualification_source (
                generation BIGINT PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS honua.alert_eval_qualification_event (
                generation BIGINT PRIMARY KEY,
                replica TEXT NOT NULL,
                evaluated_at TIMESTAMPTZ NOT NULL
            );
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task PublishAsync(NpgsqlDataSource dataSource, long generation)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO honua.alert_eval_qualification_source (generation) VALUES (@generation);",
            connection);
        command.Parameters.AddWithValue("generation", generation);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountEventsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.alert_eval_qualification_event;",
            connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<List<QualificationEvent>> ReadEventsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT generation, replica
            FROM honua.alert_eval_qualification_event
            ORDER BY generation;
            """,
            connection);
        var events = new List<QualificationEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new QualificationEvent(reader.GetInt64(0), reader.GetString(1)));
        }

        return events;
    }

    private static async Task<long> SampleCheckpointAsync(NpgsqlDataSource dataSource, List<long> samples)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(MAX(last_generation), 0)
            FROM honua.alert_worker_checkpoint
            WHERE worker_name = @worker_name;
            """,
            connection);
        command.Parameters.AddWithValue("worker_name", WorkerName);
        var value = (long)(await command.ExecuteScalarAsync() ?? 0L);
        if (samples.Count == 0 || value != samples[^1])
        {
            if (samples.Count > 0)
            {
                value.Should().BeGreaterThanOrEqualTo(samples[^1]);
            }

            samples.Add(value);
        }

        return value;
    }

    private static async Task<int> ReadLockPidAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pid
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND granted
              AND objsubid = 1
              AND classid::bigint = @class_id
              AND objid::bigint = @obj_id;
            """,
            connection);
        command.Parameters.AddWithValue("class_id", (long)PostgresAdvisoryLockLeaderElectionStrategy.LockClassId);
        command.Parameters.AddWithValue("obj_id", (long)PostgresAdvisoryLockLeaderElectionStrategy.LockObjectId);
        var pid = await command.ExecuteScalarAsync();
        return pid is int value ? value : 0;
    }

    private static async Task TerminateBackendAsync(NpgsqlDataSource dataSource, int pid)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid);", connection);
        command.Parameters.AddWithValue("pid", pid);
        var terminated = (bool?)await command.ExecuteScalarAsync() ?? false;
        terminated.Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<string> WriteEvidenceAsync(
        IReadOnlyList<string> timeline,
        IReadOnlyList<QualificationEvent> events,
        long checkpoint)
    {
        var assemblyPath = typeof(AlertEvaluationBackgroundService).Assembly.Location;
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath))).ToLowerInvariant();
        var evidence = new
        {
            issue = 3863,
            candidateSha = ReadCandidateSha(),
            packageDigest = "sha256:" + digest,
            leaderLeaseDuration = LeaderLease.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            idleDelay = IdleDelay.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            checkpoint,
            events,
            timeline,
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "3863-leader-handoff.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string ReadCandidateSha()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = directory.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                if (process.Start() && process.WaitForExit(5000) && process.ExitCode == 0)
                {
                    return process.StandardOutput.ReadToEnd().Trim();
                }

                return "unknown";
            }

            directory = directory.Parent;
        }

        return "unknown";
    }

    // Pause only the old leader's acquisition calls while its session is severed.
    // The real strategy still performs every acquisition and health decision.
    private sealed class GatedLeaderStrategy(ILeaderElectionStrategy inner) : ILeaderElectionStrategy
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public bool IsLeader => inner.IsLeader;
        public bool LastAcquireFaulted => inner.LastAcquireFaulted;
        public string InstanceId => inner.InstanceId;
        public Task PauseAsync() => _gate.WaitAsync();
        public void Resume() => _gate.Release();
        public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await inner.TryAcquireAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        public Task ReleaseAsync(CancellationToken cancellationToken = default) => inner.ReleaseAsync(cancellationToken);
    }

    private sealed record Replica(
        string Name,
        AlertEvaluationBackgroundService Service,
        PostgresAdvisoryLockLeaderElectionStrategy Strategy,
        GatedLeaderStrategy Gate,
        ServiceProvider Provider,
        AlertEvaluationHealthCheck Health,
        NumberedGenerationPipeline Pipeline) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Stopping a replica whose database session is already gone still has to release the process.
            }

            await Strategy.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private sealed record EvaluationAttempt(long Generation, string Replica, DateTimeOffset At, bool Wrote);

    private sealed record QualificationEvent(long Generation, string Replica);

    private sealed class NumberedGenerationPipeline(
        NpgsqlDataSource dataSource,
        string replica,
        ConcurrentBag<EvaluationAttempt> attempts) : IAlertPipeline
    {
        public async Task<long> ProcessChangesAsync(
            long lastProcessedGeneration,
            int batchSize,
            DateTimeOffset? evaluatedAt = null,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var select = new NpgsqlCommand(
                """
                SELECT generation
                FROM honua.alert_eval_qualification_source
                WHERE generation > @cursor
                ORDER BY generation
                LIMIT @batch;
                """,
                connection);
            select.Parameters.AddWithValue("cursor", lastProcessedGeneration);
            select.Parameters.AddWithValue("batch", batchSize);
            var generations = new List<long>();
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    generations.Add(reader.GetInt64(0));
                }
            }

            foreach (var generation in generations)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO honua.alert_eval_qualification_event (generation, replica, evaluated_at)
                    VALUES (@generation, @replica, now())
                    ON CONFLICT (generation) DO NOTHING;
                    """,
                    connection);
                insert.Parameters.AddWithValue("generation", generation);
                insert.Parameters.AddWithValue("replica", replica);
                var wrote = await insert.ExecuteNonQueryAsync(cancellationToken);
                attempts.Add(new EvaluationAttempt(generation, replica, DateTimeOffset.UtcNow, wrote == 1));
            }

            await using var max = new NpgsqlCommand(
                """
                SELECT COALESCE(MAX(generation), @cursor)
                FROM honua.alert_eval_qualification_event
                WHERE generation > @cursor;
                """,
                connection);
            max.Parameters.AddWithValue("cursor", lastProcessedGeneration);
            return (long)(await max.ExecuteScalarAsync(cancellationToken) ?? lastProcessedGeneration);
        }

        public Task<int> SweepDwellAsync(
            DateTimeOffset evaluatedAt,
            int batchSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
