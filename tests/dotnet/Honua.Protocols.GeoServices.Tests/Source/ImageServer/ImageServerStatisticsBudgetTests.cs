// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
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
