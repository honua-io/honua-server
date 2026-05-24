// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Redis Testcontainers integration coverage for the durable execution substrate
/// used by the control-plane worker and reconciler services.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisExecutionSubstrateIntegrationTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task ExecutionJobStore_WithRedis_SupportsCasUpdatesAndActiveIndexes()
    {
        await using var harness = await ControlPlaneRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-{Guid.NewGuid():N}";
        var job = CreateQueuedJob(operationId);

        var created = await harness.JobStore.TryCreateAsync(job);
        created.Should().BeTrue();

        var loaded = await harness.JobStore.GetAsync(operationId);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(1);
        loaded.Status.Should().Be(ExecutionJobStatus.Queued);

        var claimTimestamp = DateTimeOffset.UtcNow;
        var claimed = loaded with
        {
            Status = ExecutionJobStatus.Provisioning,
            ClaimedBy = "worker-integration",
            ClaimedAt = claimTimestamp,
            LastHeartbeatAt = claimTimestamp,
            CurrentPhase = "Claimed"
        };

        var casApplied = await harness.JobStore.TrySetAsync(claimed);
        casApplied.Should().BeTrue();

        var afterClaim = await harness.JobStore.GetAsync(operationId);
        afterClaim.Should().NotBeNull();
        afterClaim!.Version.Should().Be(2);
        afterClaim.Status.Should().Be(ExecutionJobStatus.Provisioning);

        var staleWriteApplied = await harness.JobStore.TrySetAsync(claimed with
        {
            Status = ExecutionJobStatus.Running,
            CurrentPhase = "stale-overwrite"
        });
        staleWriteApplied.Should().BeFalse();

        var now = DateTimeOffset.UtcNow;
        await harness.JobStore.SetAsync(afterClaim with
        {
            Status = ExecutionJobStatus.Succeeded,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Completed"
        });

        var activeJobs = await harness.JobStore.ListActiveAsync();
        activeJobs.Should().NotContain(entry => entry.OperationId == operationId);
    }

    [IntegrationTest]
    public async Task ExecutionJobStore_WithRedis_QueriesByFiltersAndCursor()
    {
        await using var harness = await ControlPlaneRedisHarness.CreateAsync(redis.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var firstId = $"job-query-first-{Guid.NewGuid():N}";
        var secondId = $"job-query-second-{Guid.NewGuid():N}";
        var otherId = $"job-query-other-{Guid.NewGuid():N}";
        var fallbackId = $"job-query-fallback-{Guid.NewGuid():N}";

        await harness.JobStore.TryCreateAsync(CreateQueuedJob(firstId) with
        {
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now.AddMinutes(-2),
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                CorrelationId = "corr-query"
            },
            Spec = CreateQueuedJob(firstId).Spec with
            {
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.DefinitionId] = "definition-query",
                    [ExecutionJobParameterKeys.Queue] = "critical",
                    [ExecutionJobParameterKeys.ResourceRefs] = "service/parcels|layer/parcels",
                    [ExecutionJobParameterKeys.TraceId] = "trace-query",
                    [ExecutionJobParameterKeys.Environment] = "test"
                }
            }
        });
        await harness.JobStore.TryCreateAsync(CreateQueuedJob(secondId) with
        {
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                CorrelationId = "corr-query"
            },
            Spec = CreateQueuedJob(secondId).Spec with
            {
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.DefinitionId] = "definition-query",
                    [ExecutionJobParameterKeys.Queue] = "critical",
                    [ExecutionJobParameterKeys.ResourceRefs] = "service/parcels",
                    [ExecutionJobParameterKeys.TraceId] = "trace-query",
                    [ExecutionJobParameterKeys.Environment] = "test"
                }
            }
        });
        await harness.JobStore.TryCreateAsync(CreateQueuedJob(otherId) with
        {
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1),
            CompletedAt = now,
            Audit = new OperationAuditInfo
            {
                RequestedBy = "bob",
                CorrelationId = "corr-other"
            },
            Spec = CreateQueuedJob(otherId).Spec with
            {
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.Queue] = "standard"
                }
            }
        });
        await harness.JobStore.TryCreateAsync(CreateQueuedJob(fallbackId) with
        {
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-4),
            UpdatedAt = now.AddMinutes(-3),
            Spec = CreateQueuedJob(fallbackId).Spec with
            {
                Backend = "fallback-queue",
                Parameters = new Dictionary<string, string>()
            }
        });

        var firstPage = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Statuses = [ExecutionJobStatus.Running],
            RequestedBy = "alice",
            CorrelationId = "corr-query",
            TraceId = "trace-query",
            DefinitionId = "definition-query",
            Queue = "critical",
            ResourceRef = "service/parcels",
            Environment = "test",
            Limit = 1
        });

        firstPage.Items.Should().ContainSingle(job => job.OperationId == secondId);
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();

        var secondPage = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Statuses = [ExecutionJobStatus.Running],
            RequestedBy = "alice",
            CorrelationId = "corr-query",
            TraceId = "trace-query",
            DefinitionId = "definition-query",
            Queue = "critical",
            ResourceRef = "service/parcels",
            Environment = "test",
            Cursor = firstPage.NextCursor,
            Limit = 1
        });

        secondPage.Items.Should().ContainSingle(job => job.OperationId == firstId);
        secondPage.NextCursor.Should().BeNull();

        var queuePage = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Queue = "critical",
            Limit = 10
        });

        queuePage.Items.Should().Contain(job => job.OperationId == firstId);
        queuePage.Items.Should().Contain(job => job.OperationId == secondId);
        queuePage.Items.Should().NotContain(job => job.OperationId == otherId);

        var fallbackQueuePage = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Queue = "fallback-queue",
            Limit = 10
        });

        fallbackQueuePage.Items.Should().ContainSingle(job => job.OperationId == fallbackId);

        var fallback = await harness.JobStore.GetAsync(fallbackId);
        await harness.JobStore.SetAsync(fallback! with
        {
            UpdatedAt = now.AddMinutes(2),
            Spec = fallback!.Spec with
            {
                Backend = "fallback-queue-next"
            }
        });

        var oldFallbackQueuePage = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Queue = "fallback-queue",
            Limit = 10
        });
        oldFallbackQueuePage.Items.Should().NotContain(job => job.OperationId == fallbackId);

        var newFallbackQueuePage = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Queue = "fallback-queue-next",
            Limit = 10
        });
        newFallbackQueuePage.Items.Should().ContainSingle(job => job.OperationId == fallbackId);

        var loaded = await harness.JobStore.GetAsync(secondId);
        await harness.JobStore.SetAsync(loaded! with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = now.AddMinutes(1),
            UpdatedAt = now.AddMinutes(1)
        });

        var running = await harness.JobStore.QueryAsync(new ExecutionJobQuery
        {
            Statuses = [ExecutionJobStatus.Running],
            Queue = "critical",
            ResourceRef = "service/parcels",
            Limit = 10
        });

        running.Items.Should().ContainSingle(job => job.OperationId == firstId);
        running.Items.Should().NotContain(job => job.OperationId == secondId);

        var invalidCursor = () => harness.JobStore.QueryAsync(new ExecutionJobQuery { Cursor = "not-a-cursor" });
        await invalidCursor.Should().ThrowAsync<ArgumentException>();
    }

    [IntegrationTest]
    public async Task ExecutionLogStore_WithRedis_AppendsInOrderAndHonoursRetention()
    {
        await using var harness = await ControlPlaneRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-log-{Guid.NewGuid():N}";

        await harness.LogStore.AppendAsync(operationId, new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = "Started execution.",
            Phase = "Queued"
        });

        await harness.LogStore.AppendAsync(operationId, new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Warning,
            Message = "Completed with warning.",
            Phase = "Completed"
        });

        var logs = await harness.LogStore.GetLogsAsync(operationId);
        logs.Select(entry => entry.Message).Should().Equal(
            "Started execution.",
            "Completed with warning.");

        var firstPage = await harness.LogStore.QueryAsync(operationId, new ExecutionLogQuery { Limit = 1 });
        firstPage.Items.Should().ContainSingle(entry => entry.Message == "Started execution.");
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();

        var secondPage = await harness.LogStore.QueryAsync(operationId, new ExecutionLogQuery
        {
            Cursor = firstPage.NextCursor,
            Limit = 1
        });
        secondPage.Items.Should().ContainSingle(entry => entry.Message == "Completed with warning.");
        secondPage.NextCursor.Should().BeNull();

        var invalidCursor = () => harness.LogStore.QueryAsync(operationId, new ExecutionLogQuery { Cursor = "not-a-cursor" });
        await invalidCursor.Should().ThrowAsync<ArgumentException>();

        await harness.LogStore.SetRetentionAsync(operationId, TimeSpan.FromMinutes(2));

        var ttl = await harness.GetTtlAsync(GetLogKey(operationId));
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
        ttl.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(30));
    }

    [IntegrationTest]
    public async Task JobExecutionService_WithRedis_CompletesJobAndPersistsArtifactsLogsAndCallbacks()
    {
        await using var harness = await ControlPlaneRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-exec-{Guid.NewGuid():N}";
        var callback = new RecordingTerminalCallback();
        var executor = new DelegatingJobExecutor(
            ExecutionJobKind.Geoprocessing,
            async (_, context, cancellationToken) =>
            {
                await context.ReportProgressAsync(42, "Running integration step", cancellationToken);
                await context.AppendLogAsync(new ExecutionLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = ExecutionLogLevel.Info,
                    Message = "Executor reached integration step.",
                    Phase = "Running"
                }, cancellationToken);
                await context.PublishArtifactAsync("s3://integration/results.geojson", cancellationToken);
                return JobExecutionResult.Succeeded();
            });

        var service = new JobExecutionService(
            harness.Queue,
            harness.JobStore,
            [executor],
            new ExecutionJobCancellationTokens(),
            [callback],
            harness.LogStore,
            NullLogger<JobExecutionService>.Instance);

        await harness.JobStore.TryCreateAsync(CreateQueuedJob(operationId));
        await harness.Queue.EnqueueAsync(operationId);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(30));
            terminal.OperationId.Should().Be(operationId);
            terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);

            var stored = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                job => job.Status == ExecutionJobStatus.Succeeded,
                TimeSpan.FromSeconds(5));

            stored.PercentComplete.Should().Be(100);
            stored.CurrentPhase.Should().Be("Completed");
            stored.ArtifactReferences.Should().ContainSingle("s3://integration/results.geojson");

            var logs = await harness.LogStore.GetLogsAsync(operationId);
            logs.Should().ContainSingle(entry =>
                entry.Level == ExecutionLogLevel.Info &&
                entry.Message == "Executor reached integration step.");

            var queueDepth = await harness.Queue.GetQueueDepthAsync();
            queueDepth.Should().Be(0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    [FlakyTest("10s wait on reconciliation callback intermittently times out under shared 'Redis' collection + Testcontainers load — tracked in #812")]
    public async Task JobReconciliationService_WithRedis_CancelsDurablyRequestedStaleJob()
    {
        await using var harness = await ControlPlaneRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-cancel-{Guid.NewGuid():N}";
        var callback = new RecordingTerminalCallback();
        var retryPolicy = new JobRetryPolicy
        {
            MaxAttempts = 3,
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero
        };
        var heartbeatPolicy = new JobHeartbeatPolicy
        {
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromMilliseconds(50)
        };

        await harness.JobStore.TryCreateAsync(CreateQueuedJob(operationId, retryPolicy, heartbeatPolicy));
        await harness.Queue.EnqueueAsync(operationId);

        var claimedId = await harness.Queue.TryClaimAsync(
            "worker-stale",
            new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing });
        claimedId.Should().Be(operationId);

        var claimed = await harness.JobStore.GetAsync(operationId);
        claimed.Should().NotBeNull();

        await harness.LogStore.AppendAsync(operationId, new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = ExecutionLogLevel.Info,
            Message = "Worker produced an early log.",
            Phase = "Running"
        });

        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        await harness.JobStore.SetAsync(claimed! with
        {
            Status = ExecutionJobStatus.Running,
            UpdatedAt = staleTimestamp,
            ClaimedAt = staleTimestamp,
            LastHeartbeatAt = staleTimestamp,
            CurrentPhase = "Running",
            CancellationRequestedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        });

        var service = new JobReconciliationService(
            harness.JobStore,
            harness.Queue,
            harness.Queue,
            new ExecutionJobCancellationTokens(),
            [callback],
            harness.LogStore,
            NullLogger<JobReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var terminal = await callback.WhenCompleted.WaitAsync(TimeSpan.FromSeconds(10));
            terminal.OperationId.Should().Be(operationId);
            terminal.Status.Should().Be(ExecutionJobStatus.Cancelled);

            var stored = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                job => job.Status == ExecutionJobStatus.Cancelled,
                TimeSpan.FromSeconds(5));

            stored.CurrentPhase.Should().Be("Cancelled");
            stored.CompletedAt.Should().NotBeNull();

            var queueDepth = await harness.Queue.GetQueueDepthAsync();
            queueDepth.Should().Be(0);

            var ttl = await harness.GetTtlAsync(GetLogKey(operationId));
            ttl.Should().NotBeNull();
            ttl!.Value.Should().BeGreaterThan(TimeSpan.FromDays(6));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task JobReconciliationService_WithRedis_RequeuesHeartbeatExpiredJobForRetry()
    {
        await using var harness = await ControlPlaneRedisHarness.CreateAsync(redis.ConnectionString);
        var operationId = $"job-retry-{Guid.NewGuid():N}";
        var callback = new RecordingTerminalCallback();
        var retryPolicy = new JobRetryPolicy
        {
            MaxAttempts = 3,
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero
        };
        var heartbeatPolicy = new JobHeartbeatPolicy
        {
            Interval = TimeSpan.FromMilliseconds(50),
            Timeout = TimeSpan.FromMilliseconds(50)
        };

        await harness.JobStore.TryCreateAsync(CreateQueuedJob(operationId, retryPolicy, heartbeatPolicy));
        await harness.Queue.EnqueueAsync(operationId);

        var claimedId = await harness.Queue.TryClaimAsync(
            "worker-retry",
            new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing });
        claimedId.Should().Be(operationId);

        var claimed = await harness.JobStore.GetAsync(operationId);
        claimed.Should().NotBeNull();

        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        await harness.JobStore.SetAsync(claimed! with
        {
            Status = ExecutionJobStatus.Running,
            UpdatedAt = staleTimestamp,
            ClaimedAt = staleTimestamp,
            LastHeartbeatAt = staleTimestamp,
            CurrentPhase = "Running"
        });

        var service = new JobReconciliationService(
            harness.JobStore,
            harness.Queue,
            harness.Queue,
            new ExecutionJobCancellationTokens(),
            [callback],
            harness.LogStore,
            NullLogger<JobReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var stored = await WaitForJobAsync(
                harness.JobStore,
                operationId,
                job => job.Status == ExecutionJobStatus.Queued && job.ClaimedBy is null,
                TimeSpan.FromSeconds(10));

            stored.CurrentPhase.Should().StartWith("Retrying");
            stored.NextRetryAt.Should().BeNull();

            callback.IsCompleted.Should().BeFalse();

            var queueDepth = await WaitForQueueDepthAsync(
                harness.Queue,
                depth => depth == 1,
                TimeSpan.FromSeconds(5));
            queueDepth.Should().Be(1);

            var reclaimed = await harness.Queue.TryClaimAsync(
                "worker-reclaim",
                new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing });
            reclaimed.Should().Be(operationId);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ExecutionJobRecord CreateQueuedJob(
        string operationId,
        JobRetryPolicy? retryPolicy = null,
        JobHeartbeatPolicy? heartbeatPolicy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            RetryPolicy = retryPolicy,
            HeartbeatPolicy = heartbeatPolicy,
            TimeoutPolicy = new JobTimeoutPolicy
            {
                MaxDuration = TimeSpan.FromMinutes(30)
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "integration-test",
                WorkloadName = "redis-substrate-integration"
            }
        };
    }

    private static async Task<ExecutionJobRecord> WaitForJobAsync(
        RedisExecutionJobStore jobStore,
        string operationId,
        Func<ExecutionJobRecord, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await jobStore.GetAsync(operationId);
            if (job != null && predicate(job))
            {
                return job;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Timed out waiting for job '{operationId}' to reach the expected state.");
    }

    private static async Task<long> WaitForQueueDepthAsync(
        RedisJobQueue queue,
        Func<long, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var depth = await queue.GetQueueDepthAsync();
            if (predicate(depth))
            {
                return depth;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("Timed out waiting for the Redis queue to reach the expected depth.");
    }

    private static string GetLogKey(string operationId) => $"controlplane:job:log:{operationId}";

    private sealed class DelegatingJobExecutor(
        ExecutionJobKind kind,
        Func<ExecutionJobRecord, IJobExecutionContext, CancellationToken, Task<JobExecutionResult>> executeAsync)
        : IJobExecutor
    {
        public ExecutionJobKind Kind { get; } = kind;

        public Task<JobExecutionResult> ExecuteAsync(
            ExecutionJobRecord job,
            IJobExecutionContext context,
            CancellationToken cancellationToken)
            => executeAsync(job, context, cancellationToken);
    }

    private sealed class RecordingTerminalCallback : IJobTerminalCallback
    {
        private readonly TaskCompletionSource<ExecutionJobRecord> _whenCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionJobRecord> WhenCompleted => _whenCompleted.Task;

        public bool IsCompleted => _whenCompleted.Task.IsCompleted;

        public ValueTask OnTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
        {
            _whenCompleted.TrySetResult(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlPlaneRedisHarness : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer _multiplexer;

        private ControlPlaneRedisHarness(ConnectionMultiplexer multiplexer)
        {
            _multiplexer = multiplexer;
            Database = _multiplexer.GetDatabase();
            Server = GetServer(_multiplexer);
            JobStore = new RedisExecutionJobStore(_multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
            LogStore = new RedisExecutionLogStore(_multiplexer, NullLogger<RedisExecutionLogStore>.Instance);
            Queue = new RedisJobQueue(_multiplexer, JobStore, NullLogger<RedisJobQueue>.Instance);
        }

        public IDatabase Database { get; }

        public IServer Server { get; }

        public RedisExecutionJobStore JobStore { get; }

        public RedisExecutionLogStore LogStore { get; }

        public RedisJobQueue Queue { get; }

        public static async Task<ControlPlaneRedisHarness> CreateAsync(string connectionString)
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var harness = new ControlPlaneRedisHarness(multiplexer);
            await harness.CleanupAsync();
            return harness;
        }

        public async Task<TimeSpan?> GetTtlAsync(string key)
            => await Database.KeyTimeToLiveAsync(key);

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
            await _multiplexer.DisposeAsync();
        }

        private async Task CleanupAsync()
        {
            var keys = Server.Keys(pattern: "controlplane:*").ToArray();
            if (keys.Length == 0)
            {
                return;
            }

            await Database.KeyDeleteAsync(keys);
        }

        private static IServer GetServer(ConnectionMultiplexer multiplexer)
        {
            var endpoints = multiplexer.GetEndPoints();
            if (endpoints.Length == 0)
            {
                throw new InvalidOperationException("Redis connection string did not provide any endpoints.");
            }

            return multiplexer.GetServer(endpoints[0]);
        }
    }
}
