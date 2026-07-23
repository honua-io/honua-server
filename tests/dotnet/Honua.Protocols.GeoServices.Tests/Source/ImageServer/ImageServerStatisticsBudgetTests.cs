// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

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

        var result = await ImageServerStatisticsBudget.ResolveAsync(
            _ => Task.FromResult(Sample),
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeSameAs(Sample);
        budgetExceeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_OperationExceedsBudget_ReturnsEmptyAndInvokesCallback()
    {
        var budgetExceeded = false;

        // Simulates a statistics computation that never completes on its own (e.g. a
        // cold-cache backfill still waiting on a large ST_SummaryStats pass): it only
        // observes its own cancellation token, exactly like the real Postgres calls
        // threaded through this budget.
        var result = await ImageServerStatisticsBudget.ResolveAsync(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Sample;
            },
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        result.Should().BeEmpty();
        budgetExceeded.Should().BeTrue();
    }

    [UnitTest]
    public async Task ResolveAsync_RequestAborted_PropagatesCancellationWithoutInvokingCallback()
    {
        var budgetExceeded = false;
        using var requestCts = new CancellationTokenSource();
        await requestCts.CancelAsync();

        var act = () => ImageServerStatisticsBudget.ResolveAsync(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Sample;
            },
            onBudgetExceeded: () => budgetExceeded = true,
            budget: TimeSpan.FromSeconds(5),
            requestCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        budgetExceeded.Should().BeFalse();
    }
}
