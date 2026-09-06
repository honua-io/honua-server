// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Real Redis/PostGIS proof for #3851. The first worker is a separate process so SIGSTOP
/// pauses its heartbeat pump and executor together; the test never edits durable timestamps,
/// attempt counts, status, or queue membership to manufacture a stale lease.
/// </summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisStaleAttemptFencingIntegrationTests(
    RedisFixture redis,
    DatabaseFixtureAdapter database,
    ITestOutputHelper output) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task StaleAttempt_IsFencedAcrossReclaimDuplicateDeliveryAndTerminalization()
    {
        await using var harness = await RedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-3851-stale-{Guid.NewGuid():N}";
        var receiptKey = $"controlplane:test:3851:receipt:{operationId}";
        var checkpointKey = $"controlplane:test:3851:checkpoint:{operationId}";
        var resumeKey = $"controlplane:test:3851:resume:{operationId}";
        var rejectedKey = $"controlplane:test:3851:rejected:{operationId}";
        var releaseKey = $"controlplane:test:3851:release:{operationId}";
        var schema = await database.CreateIsolatedSchemaAsync(nameof(RedisStaleAttemptFencingIntegrationTests));
        Process? staleWorker = null;
        var receipt = new JsonObject
        {
            ["schema"] = "honua.stale-attempt-proof/v1",
            ["operationId"] = operationId,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["assertionsPassed"] = false,
            ["cleanup"] = "not-completed"
        };

        try
        {
            await database.ExecuteDdlUnderLockAsync($"""
                CREATE TABLE "{schema}".fenced_output (
                    id BIGSERIAL PRIMARY KEY,
                    geom geometry(Geometry, 4326),
                    attributes JSONB NOT NULL,
                    idempotency_key TEXT NOT NULL
                );
                ALTER TABLE "{schema}".fenced_output
                    ADD CONSTRAINT fenced_output_idempotency UNIQUE (idempotency_key);
                """);

            var job = CreateSinkJob(operationId, schema, retryPolicy: new JobRetryPolicy
            {
                MaxAttempts = 2,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero
            }, heartbeatPolicy: new JobHeartbeatPolicy
            {
                Interval = TimeSpan.FromMilliseconds(50),
                Timeout = TimeSpan.FromMilliseconds(250)
            });
            (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
            await harness.Queue.EnqueueAsync(operationId);

            var workerAssembly = Path.Join(AppContext.BaseDirectory, "Honua.StaleAttemptWorker.dll");
            File.Exists(workerAssembly).Should().BeTrue();
            staleWorker = StartWorker(
                workerAssembly,
                redis.ConnectionString,
                operationId,
                checkpointKey,
                resumeKey,
                rejectedKey,
                receiptKey,
                database.ConnectionString,
                schema);

            var checkpoint = await WaitForRedisValueAsync(harness.Database, checkpointKey, TimeSpan.FromSeconds(15));
            checkpoint.Should().Contain("attempt=1");
            receipt["workerACheckpoint"] = checkpoint;
            var runningA = (await harness.JobStore.GetAsync(operationId))!;
            receipt["workerAClaimedAt"] = runningA.ClaimedAt?.ToString("O");
            receipt["workerAHeartbeat"] = runningA.LastHeartbeatAt?.ToString("O");
            var workerA = checkpoint[..checkpoint.IndexOf('|', StringComparison.Ordinal)];

            await SendSignalAsync("-STOP", staleWorker.Id);
            await Task.Delay(TimeSpan.FromMilliseconds(700));

            var reconciler = new JobReconciliationService(
                harness.JobStore,
                harness.Queue,
                harness.Queue,
                new ExecutionJobCancellationTokens(),
                [],
                harness.LogStore,
                NullLogger<JobReconciliationService>.Instance);
            await reconciler.SweepActiveJobsAsync(CancellationToken.None);

            var requeued = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                current => current.Status == ExecutionJobStatus.Queued && current.AttemptCount == 1,
                TimeSpan.FromSeconds(10));
            receipt["requeuedAt"] = requeued.UpdatedAt.ToString("O");
            requeued.ClaimedBy.Should().BeNull();
            requeued.ClaimedAt.Should().BeNull();
            requeued.LastHeartbeatAt.Should().BeNull();
            await harness.Database.HashSetAsync(receiptKey, "requeue", "heartbeat-expired");

            await using var terminalCallback = await CreateTerminalCallbackAsync(harness, receiptKey);
            var releaseAwareExecutor = new ReleaseAwareSinkExecutor(
                harness.Database,
                receiptKey,
                releaseKey,
                database.ConnectionString,
                schema);
            var workerB = new JobExecutionService(
                harness.Queue,
                harness.JobStore,
                [releaseAwareExecutor],
                new ExecutionJobCancellationTokens(),
                [terminalCallback],
                harness.LogStore,
                NullLogger<JobExecutionService>.Instance);
            await workerB.StartAsync(CancellationToken.None);

            string workerBId = null!;
            try
            {
                var runningB = await WaitForJobAsync(
                    harness.JobStore,
                    operationId,
                    current => current.Status == ExecutionJobStatus.Running && current.AttemptCount == 2,
                    TimeSpan.FromSeconds(10));
                runningB.ClaimedBy.Should().NotBeNull();
                workerBId = runningB.ClaimedBy!;
                workerBId.Should().NotBe(workerA);
                var heartbeatAt = runningB.LastHeartbeatAt
                    ?? throw new InvalidOperationException(
                        "Attempt 2 must publish a heartbeat timestamp once the job reports Running.");
                await harness.Database.HashSetAsync(receiptKey, "heartbeat:workerB", heartbeatAt.ToString("O"));
                await harness.Database.HashSetAsync(receiptKey, $"claim:{workerBId}", "attempt=2");
                await WaitForRedisValueAsync(harness.Database, releaseAwareExecutor.ReadyKey, TimeSpan.FromSeconds(15));

                // Duplicate delivery while attempt 2 owns the job must not claim a third attempt.
                await harness.Queue.EnqueueAsync(operationId);
                var duplicateBeforeTerminal = await harness.Queue.TryClaimAsync(
                    "duplicate-before-terminal",
                    new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing });
                duplicateBeforeTerminal.Should().BeNull();
                (await harness.JobStore.GetAsync(operationId))!.AttemptCount.Should().Be(2);
                (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
                await harness.Database.HashSetAsync(receiptKey, "duplicateBeforeTerminal", "rejected");

                await harness.Database.StringSetAsync(releaseKey, "true");
                var terminal = await terminalCallback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(15));
                terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);
                terminalCallback.InvocationCount.Should().Be(1);

                // The same duplicate delivery after finalization must also be harmless.
                await harness.Queue.EnqueueAsync(operationId);
                var duplicateAfterTerminal = await harness.Queue.TryClaimAsync(
                    "duplicate-after-terminal",
                    new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing });
                duplicateAfterTerminal.Should().BeNull();
                (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
                await harness.Database.HashSetAsync(receiptKey, "duplicateAfterTerminal", "rejected");
            }
            finally
            {
                await workerB.StopAsync(CancellationToken.None);
            }

            // Resume the exact stale process after attempt 2 is terminal. Its real executor
            // is allowed to continue, but the durable execution context must reject the sink
            // before it opens/commits the external transaction.
            await harness.Database.StringSetAsync(resumeKey, "true");
            await SendSignalAsync("-CONT", staleWorker.Id);
            await WaitForRedisValueAsync(harness.Database, $"{checkpointKey}:resumed", TimeSpan.FromSeconds(15));
            await WaitForRedisValueAsync(harness.Database, rejectedKey, TimeSpan.FromSeconds(15));
            if (!staleWorker.HasExited)
            {
                staleWorker.Kill(entireProcessTree: true);
                await staleWorker.WaitForExitAsync();
            }

            var finalJob = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                current => current.Status == ExecutionJobStatus.Succeeded,
                TimeSpan.FromSeconds(10));
            finalJob.AttemptCount.Should().Be(2);
            finalJob.ArtifactReferences.Should().ContainSingle();

            var resultStore = new RedisGeoprocessingResultPackageStore(
                harness.Multiplexer,
                TestOptions(),
                NullLogger<RedisGeoprocessingResultPackageStore>.Instance);
            var package = await resultStore.GetAsync(operationId);
            package.Should().NotBeNull();
            package!.Artifacts.Should().ContainSingle();
            var artifact = package.Artifacts.Single();
            artifact.Uri.Should().StartWith("data:application/json;base64,");
            var artifactBytes = Convert.FromBase64String(artifact.Uri!["data:application/json;base64,".Length..]);
            using var descriptor = JsonDocument.Parse(artifactBytes);
            descriptor.RootElement.GetProperty("processId").GetString().Should().Be("sink.external-postgis");
            descriptor.RootElement.GetProperty("schema").GetString().Should().Be(schema);
            descriptor.RootElement.GetProperty("table").GetString().Should().Be("fenced_output");
            descriptor.RootElement.GetProperty("featuresWritten").GetInt64().Should().Be(1);
            descriptor.RootElement.GetProperty("featuresRejected").GetInt64().Should().Be(0);
            receipt["resultPackageId"] = package.ResultPackageId;
            receipt["output"] = new JsonObject
            {
                ["artifactId"] = artifact.ArtifactId,
                ["label"] = artifact.Label,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(artifactBytes)),
                ["descriptor"] = JsonNode.Parse(artifactBytes)
            };

            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"{schema}\".fenced_output", connection);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(1);
            await using var attemptOne = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"{schema}\".fenced_output WHERE attributes->>'__pipeline_batch_id' LIKE '%:attempt:1'",
                connection);
            ((long)(await attemptOne.ExecuteScalarAsync())!).Should().Be(0);
            await using var attemptTwo = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"{schema}\".fenced_output WHERE attributes->>'__pipeline_batch_id' = @batch",
                connection);
            attemptTwo.Parameters.AddWithValue("batch", $"{operationId}:attempt:2");
            ((long)(await attemptTwo.ExecuteScalarAsync())!).Should().Be(1);

            // The fixture is one unchanged EPSG:4326 point (1, 2), not an oracle
            // derived from the sink's output. Count-only checks miss corrupted values.
            await using var values = new NpgsqlCommand(
                $"SELECT id, ST_X(geom), ST_Y(geom), ST_SRID(geom), ST_NDims(geom), attributes->>'row_id', idempotency_key FROM \"{schema}\".fenced_output", connection);
            await using (var reader = await values.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetDouble(1).Should().Be(1);
                reader.GetDouble(2).Should().Be(2);
                reader.GetInt32(3).Should().Be(4326);
                reader.GetInt32(4).Should().Be(2);
                reader.GetString(5).Should().Be("logical-1");
                reader.GetString(6).Should().Be($"{operationId}:0");
                receipt["sink"] = new JsonObject
                {
                    ["rowId"] = reader.GetInt64(0),
                    ["x"] = reader.GetDouble(1),
                    ["y"] = reader.GetDouble(2),
                    ["srid"] = reader.GetInt32(3),
                    ["dimensions"] = reader.GetInt32(4),
                    ["logicalId"] = reader.GetString(5),
                    ["idempotencyKey"] = reader.GetString(6)
                };
                (await reader.ReadAsync()).Should().BeFalse();
            }

            (await harness.Database.HashGetAsync(receiptKey, "executorInvocations")).ToString().Should().Be("2");
            (await harness.Database.HashGetAsync(receiptKey, "executorCheckpointCount")).ToString().Should().Be("1");
            (await harness.Database.HashGetAsync(receiptKey, $"invocations:{workerA}")).ToString().Should().Be("1");
            (await harness.Database.HashGetAsync(receiptKey, $"invocations:{workerBId}")).ToString().Should().Be("1");
            (await harness.Database.HashGetAsync(receiptKey, $"claim:{workerBId}")).ToString().Should().Be("attempt=2");
            (await harness.Database.HashGetAsync(receiptKey, "heartbeat:workerB")).Should().NotBe(RedisValue.Null);
            (await harness.Database.HashGetAsync(receiptKey, "terminalEvents")).ToString().Should().Be("1");
            (await harness.Database.HashGetAsync(receiptKey, "requeue")).ToString().Should().Be("heartbeat-expired");
            (await harness.Database.HashGetAsync(receiptKey, "duplicateBeforeTerminal")).ToString().Should().Be("rejected");
            (await harness.Database.HashGetAsync(receiptKey, "duplicateAfterTerminal")).ToString().Should().Be("rejected");
            receipt["assertionsPassed"] = true;
        }
        finally
        {
            if (staleWorker is { HasExited: false })
            {
                staleWorker.Kill(entireProcessTree: true);
                await staleWorker.WaitForExitAsync();
            }

            staleWorker?.Dispose();
            var events = new JsonObject();
            foreach (var entry in await harness.Database.HashGetAllAsync(receiptKey))
            {
                events[entry.Name.ToString()] = entry.Value.ToString();
            }

            receipt["events"] = events;
            receipt["staleAttemptRejection"] = (await harness.Database.StringGetAsync(rejectedKey)).ToString();
            try
            {
                RedisKey[] keys = [receiptKey, checkpointKey, $"{checkpointKey}:resumed", resumeKey,
                    rejectedKey, releaseKey, $"{releaseKey}:ready"];
                await harness.Database.KeyDeleteAsync(keys);
                foreach (var key in keys)
                {
                    (await harness.Database.KeyExistsAsync(key)).Should().BeFalse();
                }

                await database.DropSchemaAsync(schema);
                receipt["cleanup"] = "checkpoint-keys-and-sink-schema-removed";
            }
            finally
            {
                receipt["completedAt"] = DateTimeOffset.UtcNow.ToString("O");
                var directory = Environment.GetEnvironmentVariable("HONUA_SERVER_TEST_RESULTS_DIR");
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = RepositoryPaths.Resolve("tests", "TestResults");
                }

                Directory.CreateDirectory(directory);
                var path = Path.Join(directory, $"{operationId}.json");
                var json = receipt.ToJsonString();
                await File.WriteAllTextAsync(path, json);
                output.WriteLine($"Stale-attempt receipt: {path}\n{json}");
            }
        }
    }

    [IntegrationTest]
    public async Task AutomaticTransientFailure_UsesConfiguredBackoffAndStopsAtExactMaxAttempts()
    {
        await using var harness = await RedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-3851-retry-{Guid.NewGuid():N}";
        var retryPolicy = new JobRetryPolicy
        {
            MaxAttempts = 3,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(250)
        };
        var job = CreateQueuedJob(operationId, retryPolicy, new JobHeartbeatPolicy
        {
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromSeconds(2)
        });
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        await harness.Queue.EnqueueAsync(operationId);

        var callback = new CountingTerminalCallback();
        var executor = new FailingExecutor(3);
        using var worker = new JobExecutionService(
            harness.Queue,
            harness.JobStore,
            [executor],
            new ExecutionJobCancellationTokens(),
            [callback],
            harness.LogStore,
            NullLogger<JobExecutionService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var firstBackoff = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                current => current.Status == ExecutionJobStatus.Queued && current.AttemptCount == 1,
                TimeSpan.FromSeconds(10));
            firstBackoff.NextRetryAt.Should().NotBeNull();
            (firstBackoff.NextRetryAt!.Value - firstBackoff.UpdatedAt).Should().BeCloseTo(
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(25));

            var secondBackoff = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                current => current.Status == ExecutionJobStatus.Queued && current.AttemptCount == 2,
                TimeSpan.FromSeconds(10));
            secondBackoff.NextRetryAt.Should().NotBeNull();
            (secondBackoff.NextRetryAt!.Value - secondBackoff.UpdatedAt).Should().BeCloseTo(
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(25));

            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(15));
            terminal.Status.Should().Be(ExecutionJobStatus.Failed);
            executor.InvocationCount.Should().Be(retryPolicy.MaxAttempts);
            executor.Attempts.Select(attempt => attempt.AttemptCount).Should().Equal(1, 2, 3);
            executor.Attempts.Should().OnlyContain(attempt => !string.IsNullOrWhiteSpace(attempt.ClaimedBy));
            callback.InvocationCount.Should().Be(1);

            var final = await harness.JobStore.GetAsync(operationId);
            final.Should().NotBeNull();
            final!.AttemptCount.Should().Be(retryPolicy.MaxAttempts);
            final.NextRetryAt.Should().BeNull();
            final.Status.Should().Be(ExecutionJobStatus.Failed);
            (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task CancellationDuringBackoff_PreventsTheNextAutomaticAttempt()
    {
        await using var harness = await RedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-3851-cancel-backoff-{Guid.NewGuid():N}";
        var retryPolicy = new JobRetryPolicy
        {
            MaxAttempts = 3,
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(5)
        };
        var job = CreateQueuedJob(operationId, retryPolicy, new JobHeartbeatPolicy
        {
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromSeconds(2)
        });
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        await harness.Queue.EnqueueAsync(operationId);

        var executor = new FailingExecutor(3);
        using var worker = new JobExecutionService(
            harness.Queue,
            harness.JobStore,
            [executor],
            new ExecutionJobCancellationTokens(),
            [],
            harness.LogStore,
            NullLogger<JobExecutionService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var backoff = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                current => current.Status == ExecutionJobStatus.Queued && current.AttemptCount == 1,
                TimeSpan.FromSeconds(10));
            backoff.NextRetryAt.Should().NotBeNull();

            var cancelled = await ExecutionJobCancellationHelper.TryApplyAsync(
                harness.JobStore,
                operationId,
                backoff,
                "Cancelled during retry backoff");
            cancelled.State.Should().Be(ExecutionJobCancellationState.Cancelled);
            await harness.Queue.RemoveAsync(operationId);

            // Observe beyond the scheduled retry so an incorrectly resurrected job
            // cannot pass merely because cancellation initially looked terminal.
            var remainingBackoff = backoff.NextRetryAt!.Value - DateTimeOffset.UtcNow;
            if (remainingBackoff > TimeSpan.Zero)
            {
                await Task.Delay(remainingBackoff + TimeSpan.FromMilliseconds(250));
            }

            var final = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                current => current.Status == ExecutionJobStatus.Cancelled,
                TimeSpan.FromSeconds(5));
            final.NextRetryAt.Should().NotBeNull("cancellation preserves the backoff receipt on the terminal record");
            executor.InvocationCount.Should().Be(1);
            (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static ExecutionJobRecord CreateSinkJob(
        string operationId,
        string schema,
        JobRetryPolicy retryPolicy,
        JobHeartbeatPolicy heartbeatPolicy)
    {
        var input = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]},\"properties\":{\"row_id\":\"logical-1\"}}]}"));
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = ExternalPostgisSinkExecutor.HandledProcessId,
            ["protocolProcessId"] = ExternalPostgisSinkExecutor.HandledProcessId,
            [prefix + "input"] = input,
            [prefix + "connectionName"] = "test-postgis",
            [prefix + "table"] = "fenced_output",
            [prefix + "schema"] = schema,
            [prefix + "targetSrid"] = "4326",
            [prefix + "batchSize"] = "1",
            [prefix + "idempotencyColumn"] = "idempotency_key",
            [prefix + "idempotencyConstraint"] = "fenced_output_idempotency",
            [prefix + "idempotencyKey"] = operationId
        };

        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            RetryPolicy = retryPolicy,
            HeartbeatPolicy = heartbeatPolicy,
            TimeoutPolicy = new JobTimeoutPolicy { MaxDuration = TimeSpan.FromMinutes(5) },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                Backend = "integration",
                WorkloadName = "sink.external-postgis",
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Parameters = parameters
            }
        };
    }

    private static ExecutionJobRecord CreateQueuedJob(
        string operationId,
        JobRetryPolicy retryPolicy,
        JobHeartbeatPolicy heartbeatPolicy)
        => new()
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            RetryPolicy = retryPolicy,
            HeartbeatPolicy = heartbeatPolicy,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                Backend = "integration",
                WorkloadName = "retry-proof",
                TargetKind = BatchComputeTargetKind.KubernetesJob
            }
        };

    private async Task<CountingTerminalCallback> CreateTerminalCallbackAsync(
        RedisHarness harness,
        string receiptKey)
    {
        var options = TestOptions();
        var packageStore = new RedisGeoprocessingResultPackageStore(
            harness.Multiplexer,
            options,
            NullLogger<RedisGeoprocessingResultPackageStore>.Instance);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var callback = new GeoprocessingJobTerminalCallback(
            SubstituteProgressStore(),
            new BuiltInProcessCatalog(),
            options,
            packageStore,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GeoprocessingJobTerminalCallback>.Instance);
        return new CountingTerminalCallback(callback, serviceProvider, harness.Database, receiptKey);
    }

    private static IUniversalProgressStore SubstituteProgressStore()
        => NSubstitute.Substitute.For<IUniversalProgressStore>();

    private static IOptionsMonitor<GeoprocessingExecutorOptions> TestOptions()
    {
        var monitor = NSubstitute.Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        return monitor;
    }

    private static Process StartWorker(string assembly, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start stale-attempt worker.");
    }

    private static async Task SendSignalAsync(string signal, int processId)
    {
        using var signalProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/kill",
            UseShellExecute = false,
            ArgumentList = { signal, processId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        }) ?? throw new InvalidOperationException("Could not start kill.");
        await signalProcess.WaitForExitAsync();
        signalProcess.ExitCode.Should().Be(0);
    }

    private static async Task<string> WaitForRedisValueAsync(
        IDatabase database,
        string key,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await database.StringGetAsync(key);
            if (value.HasValue)
            {
                return value.ToString();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for Redis key '{key}'.");
    }

    private static async Task<ExecutionJobRecord> WaitForJobAsync(
        RedisExecutionJobStore store,
        string operationId,
        Func<ExecutionJobRecord, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await store.GetAsync(operationId);
            if (job != null && predicate(job))
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for job '{operationId}'.");
    }

    private sealed class ReleaseAwareSinkExecutor(
        IDatabase database,
        string receiptKey,
        string releaseKey,
        string postgresConnectionString,
        string schema) : IJobExecutor
    {
        private readonly ExternalPostgisSinkExecutor _sink = new(
            TestOptions(),
            new StaticConnectionResolver(postgresConnectionString),
            NullLogger<ExternalPostgisSinkExecutor>.Instance);

        public string ReadyKey { get; } = $"{releaseKey}:ready";
        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

        public async Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            var workerId = job.ClaimedBy ?? "unknown-worker";
            await database.HashIncrementAsync(receiptKey, $"invocations:{workerId}");
            await database.HashIncrementAsync(receiptKey, "executorInvocations");
            var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
            var parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal)
            {
                [prefix + "batchId"] = $"{job.OperationId}:attempt:{job.AttemptCount}",
                [prefix + "schema"] = schema
            };
            var result = await _sink.ExecuteAsync(
                job with { Spec = job.Spec with { Parameters = parameters } },
                context,
                cancellationToken);
            await database.StringSetAsync(ReadyKey, "true");
            while (!await database.KeyExistsAsync(releaseKey))
            {
                await Task.Delay(50, cancellationToken);
            }

            return result;
        }
    }

    private sealed class FailingExecutor(int failures) : IJobExecutor
    {
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public ConcurrentQueue<ExecutionJobRecord> Attempts { get; } = new();
        public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;
        private int _invocationCount;

        public Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
        {
            Attempts.Enqueue(job);
            var invocation = Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(invocation <= failures
                ? JobExecutionResult.Failed($"transient failure {invocation}")
                : JobExecutionResult.Succeeded());
        }
    }

    private sealed class CountingTerminalCallback : IJobTerminalCallback, IAsyncDisposable
    {
        private readonly IJobTerminalCallback? _inner;
        private readonly ServiceProvider? _serviceProvider;
        private readonly IDatabase? _receiptDatabase;
        private readonly string? _receiptKey;
        private readonly TaskCompletionSource<ExecutionJobRecord> _whenCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public CountingTerminalCallback(
            IJobTerminalCallback? inner = null,
            ServiceProvider? serviceProvider = null,
            IDatabase? receiptDatabase = null,
            string? receiptKey = null)
        {
            _inner = inner;
            _serviceProvider = serviceProvider;
            _receiptDatabase = receiptDatabase;
            _receiptKey = receiptKey;
        }

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public Task<ExecutionJobRecord> WhenCompleted => _whenCompleted.Task;

        public async ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            if (_receiptDatabase != null && _receiptKey != null)
            {
                await _receiptDatabase.HashIncrementAsync(_receiptKey, "terminalEvents");
            }

            if (_inner != null)
            {
                await _inner.OnTerminalAsync(job, cancellationToken);
            }

            _whenCompleted.TrySetResult(job);
        }

        public ValueTask DisposeAsync()
        {
            _serviceProvider?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticConnectionResolver(string connectionString) : Honua.Core.Features.Security.Abstractions.ISecureConnectionResolver
    {
        public Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(connectionString);
        public Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(connectionString);
        public Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["test-postgis"]);
    }

    private sealed class RedisHarness : IAsyncDisposable
    {
        private RedisHarness(ConnectionMultiplexer multiplexer)
        {
            Multiplexer = multiplexer;
            Database = multiplexer.GetDatabase();
            Server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
            JobStore = new RedisExecutionJobStore(multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
            LogStore = new RedisExecutionLogStore(multiplexer, NullLogger<RedisExecutionLogStore>.Instance);
            Queue = new RedisJobQueue(multiplexer, JobStore, NullLogger<RedisJobQueue>.Instance);
        }

        public ConnectionMultiplexer Multiplexer { get; }
        public IDatabase Database { get; }
        public IServer Server { get; }
        public RedisExecutionJobStore JobStore { get; }
        public RedisExecutionLogStore LogStore { get; }
        public RedisJobQueue Queue { get; }

        public static async Task<RedisHarness> CreateAsync(string connectionString)
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var harness = new RedisHarness(multiplexer);
            await harness.Database.KeyDeleteAsync(harness.Server.Keys(pattern: "controlplane:*").ToArray());
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await Database.KeyDeleteAsync(Server.Keys(pattern: "controlplane:*").ToArray());
            await Multiplexer.DisposeAsync();
        }
    }
}
