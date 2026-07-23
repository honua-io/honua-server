// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.DependencyInjection;

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
/// request-visible wait is deliberately decoupled from that operation, which runs in an
/// independently owned dependency-injection scope. Timing out this helper returns an empty
/// response but does not cancel or prematurely dispose the single-flight backfill or its
/// database-admission lease. Later requests can therefore hit the rows it persisted instead
/// of repeatedly starting and rolling back the same expensive computation.
/// </remarks>
internal static class ImageServerStatisticsBudget
{
    private static class PersistedBackfills<T>
    {
        internal static readonly ConcurrentDictionary<string, Lazy<Task<T[]>>> ByKey =
            new(StringComparer.Ordinal);
    }

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
        IServiceScopeFactory scopeFactory,
        string operationKey,
        string? schemaName,
        Func<IServiceProvider, CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        CancellationToken requestAborted)
        => ResolveAsync(
            scopeFactory, operationKey, schemaName, operation,
            onBudgetExceeded, Timeout, requestAborted);

    /// <summary>
    /// Runs non-persisted work, such as histogram scans, within the response budget.
    /// Unlike the persisted statistics overload, exceeding the budget cancels the
    /// operation because allowing fresh SQL to outlive the response provides no cache
    /// benefit and would multiply database work on retries.
    /// </summary>
    public static Task<T[]> ResolveCancellableAsync<T>(
        Func<CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        CancellationToken requestAborted)
        => ResolveCancellableAsync(operation, onBudgetExceeded, Timeout, requestAborted);

    /// <summary>
    /// Test seam: identical to the public <c>ResolveAsync</c> overload
    /// but with an explicit budget, so tests can exercise the timeout/fallback path in
    /// milliseconds instead of waiting out the real <see cref="Timeout"/>.
    /// </summary>
    internal static async Task<T[]> ResolveAsync<T>(
        IServiceScopeFactory scopeFactory,
        string operationKey,
        string? schemaName,
        Func<IServiceProvider, CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        TimeSpan budget,
        CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

        // The backfill owns an independent scope so request completion cannot dispose its
        // Postgres provider or release query-admission slots while SQL is still running.
        // Do not pass the request/budget token into it: Postgres uses that token for both
        // ST_SummaryStats and transaction commit, and cancelling it at the response deadline
        // would roll back the single-flight backfill forever.
        // Share one owned operation per exact raster/mosaic key. Postgres serializes the
        // persisted computation with an advisory lock, but that lock alone would leave
        // every duplicate caller holding a scope and connection while queued. Only the
        // dictionary winner starts work; all other callers wait on the same task.
        var candidate = new Lazy<Task<T[]>>(
            () => RunInOwnedScopeAsync(scopeFactory, schemaName, operation),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = PersistedBackfills<T>.ByKey.GetOrAdd(operationKey, candidate);
        var operationTask = shared.Value;
        if (ReferenceEquals(shared, candidate))
        {
            _ = operationTask.ContinueWith(
                _ => PersistedBackfills<T>.ByKey.TryRemove(
                    new KeyValuePair<string, Lazy<Task<T[]>>>(operationKey, shared)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

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

    internal static string CreateStatisticsOperationKey(
        string? schemaName,
        int layerId,
        IEnumerable<long> rasterIds,
        RasterMergeStrategy mergeStrategy)
    {
        var schemaKey = schemaName is null
            ? "<default>"
            : schemaName.Replace("|", "||", StringComparison.Ordinal);
        return $"image-statistics:{schemaKey}|{layerId}|{(int)mergeStrategy}|{string.Join(",", rasterIds)}";
    }

    /// <summary>Test seam for the cancellable, non-persisted operation policy.</summary>
    internal static async Task<T[]> ResolveCancellableAsync<T>(
        Func<CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        TimeSpan budget,
        CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var operationTask = operation(operationCts.Token);
        try
        {
            return await operationTask.WaitAsync(budget, requestAborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await operationCts.CancelAsync().ConfigureAwait(false);
            ObserveBackgroundFault(operationTask);
            onBudgetExceeded();
            return [];
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            await operationCts.CancelAsync().ConfigureAwait(false);
            ObserveBackgroundFault(operationTask);
            throw;
        }
    }

    private static async Task<T[]> RunInOwnedScopeAsync<T>(
        IServiceScopeFactory scopeFactory,
        string? schemaName,
        Func<IServiceProvider, CancellationToken, Task<T[]>> operation)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var schemaContext = scope.ServiceProvider.GetService<SchemaContext>();
        if (schemaName is not null && schemaContext is null)
        {
            throw new InvalidOperationException(
                $"The owned statistics scope cannot restore schema context '{schemaName}'.");
        }

        var previousSchema = schemaContext?.CurrentSchema;
        try
        {
            if (schemaContext is not null)
            {
                schemaContext.CurrentSchema = schemaName;
            }

            return await operation(scope.ServiceProvider, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (schemaContext is not null)
            {
                schemaContext.CurrentSchema = previousSchema;
            }
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
