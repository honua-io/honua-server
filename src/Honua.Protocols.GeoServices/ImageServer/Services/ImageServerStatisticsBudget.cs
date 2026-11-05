// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// The request wait is separate from the statistics backfill: timing out returns an empty response and does not cancel the single-flight computation or its database lease.
internal static class ImageServerStatisticsBudget
{
    private static class PersistedBackfills<T>
    {
        internal static readonly ConcurrentDictionary<string, Lazy<Task<T[]>>> ByKey =
            new(StringComparer.Ordinal);
    }

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // The incomplete fallback is replaced by the background backfill moments later, so no cache may keep it.
    public static Action MarkResponseIncomplete(HttpResponse response, ILogger logger, int layerId)
        => () =>
        {
            response.Headers.CacheControl = "no-store";
            ImageServerLog.StatisticsComputeBudgetExceeded(logger, layerId, Timeout.TotalSeconds);
        };

    // A budget timeout returns an empty array instead of the cancellation, while a client disconnect still propagates.
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

    // A histogram scan that exceeds the response budget is cancelled, because letting that SQL outlive the response does not fill a cache.
    public static Task<T[]> ResolveCancellableAsync<T>(
        Func<CancellationToken, Task<T[]>> operation,
        Action onBudgetExceeded,
        CancellationToken requestAborted)
        => ResolveCancellableAsync(operation, onBudgetExceeded, Timeout, requestAborted);

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
        catch (RasterStatisticsBudgetExceededException)
        {
            onBudgetExceeded();
            return [];
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
        RasterMergeStrategy mergeStrategy,
        int[]? bands = null)
    {
        var schemaKey = schemaName is null
            ? "<default>"
            : schemaName.Replace("|", "||", StringComparison.Ordinal);
        var selectionKey = bands is null ? string.Empty : $"|bands:{string.Join(",", bands)}";
        return $"image-statistics:{schemaKey}|{layerId}|{(int)mergeStrategy}|{string.Join(",", rasterIds)}{selectionKey}";
    }

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
