// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Middleware;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for <see cref="ImageServerStatisticsBudget"/> (#2991): a slow/cold-cache
/// statistics computation must degrade to an empty result within a bounded time
/// budget rather than hang the request indefinitely.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerStatisticsBudgetTests
{
    private static readonly RasterStatistics[] Sample =
    [
        new() { Band = 1, MinValue = 0, MaxValue = 255, MeanValue = 128, StandardDeviation = 10 }
    ];

    [UnitTest]
    public async Task ResolveAsync_OperationCompletesWithinBudget_ReturnsComputedStatistics()
    {
        var budgetExceeded = false;
        using var services = new ServiceCollection().BuildServiceProvider();

        var result = await ImageServerStatisticsBudget.ResolveAsync(
            services.GetRequiredService<IServiceScopeFactory>(),
            $"test:{Guid.NewGuid()}",
            schemaName: null,
            (_, _) => Task.FromResult(Sample),
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeSameAs(Sample);
        budgetExceeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_OperationExceedsBudget_ReturnsEmptyWithoutCancellingBackfill()
    {
        var budgetExceeded = false;
        var backfill = new TaskCompletionSource<RasterStatistics[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeStarted = new TaskCompletionSource<ScopeLifetime>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        using var services = new ServiceCollection()
            .AddScoped<ScopeLifetime>()
            .BuildServiceProvider();

        var result = await ImageServerStatisticsBudget.ResolveAsync(
            services.GetRequiredService<IServiceScopeFactory>(),
            $"test:{Guid.NewGuid()}",
            schemaName: null,
            (provider, ct) =>
            {
                operationToken = ct;
                scopeStarted.SetResult(provider.GetRequiredService<ScopeLifetime>());
                return backfill.Task;
            },
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        result.Should().BeEmpty();
        budgetExceeded.Should().BeTrue();
        operationToken.Should().Be(CancellationToken.None);
        backfill.Task.IsCompleted.Should().BeFalse("the persistence backfill must outlive the response budget");
        var scopeLifetime = await scopeStarted.Task;
        scopeLifetime.Disposed.Task.IsCompleted.Should().BeFalse(
            "the background scope must retain its database admission until the backfill finishes");

        backfill.SetResult(Sample);
        (await backfill.Task).Should().BeSameAs(Sample);
        await scopeLifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnitTest]
    public async Task ResolveAsync_RequestAborted_PropagatesCancellationWithoutInvokingCallback()
    {
        var budgetExceeded = false;
        var backfill = new TaskCompletionSource<RasterStatistics[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeStarted = new TaskCompletionSource<ScopeLifetime>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var requestCts = new CancellationTokenSource();
        using var services = new ServiceCollection()
            .AddScoped<ScopeLifetime>()
            .BuildServiceProvider();
        await requestCts.CancelAsync();

        var act = () => ImageServerStatisticsBudget.ResolveAsync(
            services.GetRequiredService<IServiceScopeFactory>(),
            $"test:{Guid.NewGuid()}",
            schemaName: null,
            (provider, _) =>
            {
                scopeStarted.SetResult(provider.GetRequiredService<ScopeLifetime>());
                return backfill.Task;
            },
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromSeconds(5),
            requestCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        budgetExceeded.Should().BeFalse();
        var scopeLifetime = await scopeStarted.Task;
        scopeLifetime.Disposed.Task.IsCompleted.Should().BeFalse(
            "request cancellation must not release admission while the backfill is still running");

        backfill.SetResult(Sample);
        (await backfill.Task).Should().BeSameAs(Sample);
        await scopeLifetime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnitTest]
    public async Task ResolveAsync_ConcurrentSameKey_StartsOneOwnedBackfill()
    {
        var operationKey = $"test:{Guid.NewGuid()}";
        var backfill = new TaskCompletionSource<RasterStatistics[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operationCount = 0;
        var scopeStarted = new TaskCompletionSource<ScopeLifetime>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = new ServiceCollection()
            .AddScoped<ScopeLifetime>()
            .BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

        Task<RasterStatistics[]> Resolve() => ImageServerStatisticsBudget.ResolveAsync(
            scopeFactory,
            operationKey,
            schemaName: null,
            (provider, _) =>
            {
                Interlocked.Increment(ref operationCount);
                scopeStarted.TrySetResult(provider.GetRequiredService<ScopeLifetime>());
                started.TrySetResult(true);
                return backfill.Task;
            },
            onBudgetExceeded: () => { },
            budget: TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        var first = Resolve();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Resolve();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.Length == 0);
        operationCount.Should().Be(1, "waiters for one mosaic must share its owned backfill");

        backfill.SetResult(Sample);
        (await backfill.Task).Should().BeSameAs(Sample);
        await (await scopeStarted.Task).Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnitTest]
    public async Task ResolveAsync_OwnedScopeRestoresSchemaAndPartitionsKey()
    {
        const string schemaName = "tenant_alpha";
        using var services = new ServiceCollection()
            .AddScoped<SchemaContext>()
            .AddScoped<ISchemaContext>(provider => provider.GetRequiredService<SchemaContext>())
            .BuildServiceProvider();

        string? observedSchema = null;
        var key = ImageServerStatisticsBudget.CreateStatisticsOperationKey(
            schemaName, 7, [101, 102], RasterMergeStrategy.Newest);
        var otherTenantKey = ImageServerStatisticsBudget.CreateStatisticsOperationKey(
            "tenant_beta", 7, [101, 102], RasterMergeStrategy.Newest);

        var result = await ImageServerStatisticsBudget.ResolveAsync(
            services.GetRequiredService<IServiceScopeFactory>(),
            key,
            schemaName,
            (provider, _) =>
            {
                observedSchema = provider.GetRequiredService<ISchemaContext>().CurrentSchema;
                return Task.FromResult(Sample);
            },
            onBudgetExceeded: () => { },
            budget: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeSameAs(Sample);
        observedSchema.Should().Be(schemaName);
        key.Should().NotBe(otherTenantKey);
    }

    [UnitTest]
    public async Task ResolveCancellableAsync_OperationExceedsBudget_CancelsNonPersistedWork()
    {
        var budgetExceeded = false;
        var cancellationObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await ImageServerStatisticsBudget.ResolveCancellableAsync<RasterStatistics>(
            async ct =>
            {
                using var registration = ct.Register(
                    () => cancellationObserved.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Sample;
            },
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        result.Should().BeEmpty();
        budgetExceeded.Should().BeTrue();
        (await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    private sealed class ScopeLifetime : IDisposable
    {
        public ScopeLifetime()
        {
        }

        public TaskCompletionSource<bool> Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => Disposed.TrySetResult(true);
    }
}
