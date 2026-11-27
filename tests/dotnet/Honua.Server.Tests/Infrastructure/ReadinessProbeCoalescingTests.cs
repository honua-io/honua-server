// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(TestProtocols.Health)]
public sealed class ReadinessProbeCoalescingTests
{
    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_ConcurrentCallers_CoalescesIntoSingleDatabaseProbe()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new CountingGatedDatabaseChecker(gate.Task, entered);
        var state = new MigrationState();
        state.MarkSucceeded();
        var service = new ReadinessCheckService(
            checker,
            state,
            new MockLogger<ReadinessCheckService>())
        {
            TimeProvider = new ControllableTimeProvider()
        };

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => service.CheckReadinessAsync())
            .ToArray();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.SetResult(true);

        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(result => result.IsReady);
        checker.InvocationCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithinCacheTtl_ReusesResultAndRefreshesAfterExpiry()
    {
        var timeProvider = new ControllableTimeProvider();
        var checker = new CountingDatabaseChecker();
        var state = new MigrationState();
        state.MarkSucceeded();
        var service = new ReadinessCheckService(
            checker,
            state,
            new MockLogger<ReadinessCheckService>())
        {
            TimeProvider = timeProvider
        };

        var first = await service.CheckReadinessAsync();
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        var cached = await service.CheckReadinessAsync();
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        var refreshed = await service.CheckReadinessAsync();

        first.IsReady.Should().BeTrue();
        cached.IsReady.Should().BeTrue();
        refreshed.IsReady.Should().BeTrue();
        checker.InvocationCount.Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WhenOneConcurrentCallerCancels_OtherCallerStillCompletes()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new CountingGatedDatabaseChecker(gate.Task, entered);
        var state = new MigrationState();
        state.MarkSucceeded();
        var service = new ReadinessCheckService(
            checker,
            state,
            new MockLogger<ReadinessCheckService>())
        {
            TimeProvider = new ControllableTimeProvider()
        };

        using var firstCts = new CancellationTokenSource();
        var firstCall = service.CheckReadinessAsync(firstCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondCall = service.CheckReadinessAsync();

        await firstCts.CancelAsync();
        await FluentActions.Invoking(() => firstCall).Should().ThrowAsync<OperationCanceledException>();

        gate.SetResult(true);
        var secondResult = await secondCall;
        secondResult.IsReady.Should().BeTrue();
        checker.InvocationCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CoalescingReadinessCheckService_DegradedRecoveryTransitionsImmediatelyAndCoalescesDbProbe()
    {
        var degraded = new DegradedStartupContext();
        degraded.MarkDegraded(true, typeof(ReadinessProbeCoalescingTests).Assembly, "startup transient",
            reasonCode: ReadinessReasonCodes.DatabaseStartupDegraded);
        var migrationState = new MigrationState();
        var checker = new CountingDatabaseChecker();
        var timeProvider = new ControllableTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IDatabaseHealthChecker>(checker);
        services.AddSingleton(migrationState);
        services.AddSingleton(degraded);
        services.AddSingleton<ILogger<ReadinessCheckService>>(new MockLogger<ReadinessCheckService>());
        services.AddScoped<ReadinessCheckService>();
        using var provider = services.BuildServiceProvider();
        var coalescing = new CoalescingReadinessCheckService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider);

        var whileDegraded = await coalescing.CheckReadinessAsync();
        whileDegraded.IsReady.Should().BeFalse();
        whileDegraded.ReasonCode.Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);
        checker.InvocationCount.Should().Be(0);

        degraded.MarkRecovered();
        migrationState.MarkSucceeded();
        var afterRecovery = await coalescing.CheckReadinessAsync();
        var cachedProbe = await coalescing.CheckReadinessAsync();

        afterRecovery.IsReady.Should().BeTrue();
        cachedProbe.IsReady.Should().BeTrue();
        checker.InvocationCount.Should().Be(1);
    }

    private sealed class CountingDatabaseChecker : IDatabaseHealthChecker
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(true);
        }
    }

    private sealed class CountingGatedDatabaseChecker(
        Task<bool> gate,
        TaskCompletionSource entered) : IDatabaseHealthChecker
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            entered.TrySetResult();
            return gate;
        }
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerSecond * 100;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
