// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Bounds the cost of computing (and persisting) raster mosaic statistics/histograms so
/// a large, cold-cache mosaic degrades gracefully within a fixed, short time budget
/// instead of blocking the request until an outer host-level deadline (a serverless
/// function timeout, a load-balancer idle timeout, ...) kills the whole connection with
/// an opaque, unbounded hang (#2991).
/// </summary>
/// <remarks>
/// The Postgres raster store's statistics backfill already grants itself a generous
/// compute budget (300s) so a big <c>ST_SummaryStats</c> pass can finish and persist for
/// <em>future</em> requests rather than timing out mid-computation (#1649). The
/// request-visible wait is deliberately decoupled from that operation: timing out this
/// helper returns an empty response but does not cancel the single-flight backfill. Later
/// requests can therefore hit the rows it persisted instead of repeatedly starting and
/// rolling back the same expensive computation.
/// </remarks>
internal static class ImageServerStatisticsBudget
{
    /// <summary>
    /// Maximum time a single request will wait for raster statistics/histograms before
    /// falling back to an empty result. Comfortably below the platform's default
    /// request/function timeouts (60s) so the response always returns well inside typical
    /// client and infrastructure timeouts instead of racing them.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs <paramref name="operation"/> with a bounded time budget layered on top of
    /// <paramref name="requestAborted"/>. If the operation does not complete within
    /// <see cref="Timeout"/>, <paramref name="onBudgetExceeded"/> is invoked and an empty
    /// array is returned instead of propagating the cancellation — the caller still gets
    /// a valid (if statistics/histogram-less) response rather than an exception. A client
    /// disconnect (<paramref name="requestAborted"/> itself firing) is not treated as a
    /// budget timeout and still propagates as a normal cancellation.
    /// </summary>
    public static Task<T[]> ResolveAsync<T>(
        Func<CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        CancellationToken requestAborted)
        => ResolveAsync(operation, onBudgetExceeded, Timeout, requestAborted);

    /// <summary>
    /// Test seam: identical to <see cref="ResolveAsync{T}(Func{CancellationToken,Task{T[]}},Action,CancellationToken)"/>
    /// but with an explicit budget, so tests can exercise the timeout/fallback path in
    /// milliseconds instead of waiting out the real <see cref="Timeout"/>.
    /// </summary>
    internal static async Task<T[]> ResolveAsync<T>(
        Func<CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        TimeSpan budget,
        CancellationToken requestAborted)
    {
        // Do not pass the request/budget token into the store operation. The Postgres
        // implementation uses that token for ST_SummaryStats *and* transaction commit;
        // cancelling it at the response deadline would roll back the single-flight
        // backfill and force every later request to repeat it forever.
        var operationTask = operation(CancellationToken.None);

        try
        {
            return await operationTask.WaitAsync(budget, requestAborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveBackgroundFault(operationTask);
            onBudgetExceeded();
            return [];
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            ObserveBackgroundFault(operationTask);
            throw;
        }
    }

    private static void ObserveBackgroundFault<T>(Task<T[]> operationTask)
    {
        _ = operationTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
