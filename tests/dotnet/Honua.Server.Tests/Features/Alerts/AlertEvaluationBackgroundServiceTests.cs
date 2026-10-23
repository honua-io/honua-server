// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Checkpoint reload on leadership changes, and a startup read failure that must not
/// stop the host (#6190).
/// </summary>
public sealed class AlertEvaluationBackgroundServiceTests
{
    [UnitTest]
    public async Task Execute_ReloadsCheckpointWhenLeadershipIsAcquired()
    {
        var leader = new ScriptedLeader();
        var store = new MutableCheckpointStore(initialGeneration: 10);
        var pipeline = new RecordingPipeline();
        await using var provider = CreateProvider(store, pipeline);
        var service = CreateService(provider, leader);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => leader.Calls >= 1);
            store.SetGeneration(5_000_000);
            leader.Allow = true;
            await WaitUntilAsync(() => !pipeline.Generations.IsEmpty);

            pipeline.Generations.TryPeek(out var generation).Should().BeTrue();
            generation.Should().Be(5_000_000);
            service.ExecuteTask.Should().NotBeNull();
            service.ExecuteTask!.IsFaulted.Should().BeFalse();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    public async Task Execute_CheckpointReadThrowsOnce_KeepsRunningAndInvokesPipeline()
    {
        var leader = new ScriptedLeader { Allow = true };
        var store = new FlakyCheckpointStore(failuresBeforeSuccess: 1);
        var pipeline = new RecordingPipeline();
        await using var provider = CreateProvider(store, pipeline);
        var service = CreateService(provider, leader);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => !pipeline.Generations.IsEmpty);

            pipeline.Generations.TryPeek(out var generation).Should().BeTrue();
            generation.Should().Be(42);
            service.ExecuteTask.Should().NotBeNull();
            service.ExecuteTask!.IsFaulted.Should().BeFalse();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        service.ExecuteTask!.IsFaulted.Should().BeFalse();
        service.ExecuteTask.IsCompleted.Should().BeTrue();
    }

    [UnitTest]
    public async Task Execute_PersistedEvaluationInstant_FollowsSharedWallClockWithoutSleeping()
    {
        var time = new ScriptedTimeProvider();
        var leader = new ScriptedLeader { Allow = true };
        var store = new MutableCheckpointStore(initialGeneration: 10);
        var pipeline = new RecordingPipeline();
        await using var provider = CreateProvider(store, pipeline);
        var service = CreateService(provider, leader, time);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => pipeline.EvaluatedAt == time.Wall);

            var jumped = time.Wall.AddHours(2);
            time.Wall = jumped;
            await WaitUntilAsync(() => pipeline.EvaluatedAt == jumped);
            pipeline.EvaluatedAt.Should().Be(jumped);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider CreateProvider(IAlertCheckpointStore store, IAlertPipeline pipeline)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(pipeline);
        return services.BuildServiceProvider();
    }

    private static AlertEvaluationBackgroundService CreateService(
        ServiceProvider provider,
        ILeaderElectionStrategy leader,
        TimeProvider? time = null)
    {
        var options = Options.Create(new AlertOptions
        {
            Enabled = true,
            Evaluation = new AlertEvaluationOptions
            {
                WorkerName = "evaluator",
                IdleDelay = TimeSpan.FromMilliseconds(20),
                DwellSweepInterval = TimeSpan.FromHours(1),
                ChangeBatchSize = 10,
            },
        });
        return new AlertEvaluationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            leader,
            options,
            TestTelemetry.CreateAlertPipelineMetrics(),
            NullLogger<AlertEvaluationBackgroundService>.Instance,
            time);
    }

    private sealed class ScriptedTimeProvider : TimeProvider
    {
        public DateTimeOffset Wall { get; set; } = new(2026, 3, 8, 10, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Wall;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ScriptedLeader : ILeaderElectionStrategy
    {
        private int _calls;

        public volatile bool Allow;

        public int Calls => Volatile.Read(ref _calls);

        public bool IsLeader { get; private set; }

        public bool LastAcquireFaulted { get; private set; }

        public string InstanceId => "test-leader";

        public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var leader = Allow;
            IsLeader = leader;
            LastAcquireFaulted = false;
            return Task.FromResult(leader);
        }

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            IsLeader = false;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableCheckpointStore(long initialGeneration) : IAlertCheckpointStore
    {
        private long _generation = initialGeneration;

        public void SetGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);

        public Task<AlertWorkerCheckpoint> GetAsync(string workerName, CancellationToken cancellationToken = default)
            => Task.FromResult(new AlertWorkerCheckpoint
            {
                WorkerName = workerName,
                LastGeneration = Interlocked.Read(ref _generation),
            });

        public Task SetAsync(AlertWorkerCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _generation, checkpoint.LastGeneration);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyCheckpointStore(int failuresBeforeSuccess) : IAlertCheckpointStore
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public Task<AlertWorkerCheckpoint> GetAsync(string workerName, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                throw new NpgsqlException("database unavailable");
            }

            return Task.FromResult(new AlertWorkerCheckpoint
            {
                WorkerName = workerName,
                LastGeneration = 42,
            });
        }

        public Task SetAsync(AlertWorkerCheckpoint checkpoint, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingPipeline : IAlertPipeline
    {
        public ConcurrentQueue<long> Generations { get; } = new();

        public DateTimeOffset? EvaluatedAt { get; private set; }

        public Task<long> ProcessChangesAsync(
            long lastProcessedGeneration,
            int batchSize,
            DateTimeOffset? evaluatedAt = null,
            CancellationToken cancellationToken = default)
        {
            EvaluatedAt = evaluatedAt;
            Generations.Enqueue(lastProcessedGeneration);
            return Task.FromResult(lastProcessedGeneration);
        }

        public Task<int> SweepDwellAsync(
            DateTimeOffset evaluatedAt,
            int batchSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
