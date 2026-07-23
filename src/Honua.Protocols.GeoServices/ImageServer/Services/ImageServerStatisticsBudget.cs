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
/// <em>future</em> requests rather than timing out mid-computation (#1649). That budget
/// is only useful if something actually waits for it; a request-scoped host (Lambda,
/// a reverse-proxy idle timeout, ...) is very often shorter than 300s, so the very first
/// cold-cache request against a large mosaic can never finish in time and simply repeats
/// the same expensive, never-completing computation forever. Capping the request-visible
/// wait well under any realistic host deadline turns that infinite failure loop into a
/// fast, predictable response (metadata without statistics/histograms) on the first few
/// calls while the persisted-stats backfill keeps a chance to complete out from under the
/// tighter budget on a request that happens to hit a warm cache.
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
        using var budgetCts = new CancellationTokenSource(budget);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, budgetCts.Token);

        try
        {
            return await operation(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            onBudgetExceeded();
            return [];
        }
    }
}
