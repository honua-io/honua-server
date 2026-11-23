// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

// One request that reads many layers runs at most MaxConcurrency of those reads at a time, so the
// number of published layers does not decide how many database connections a single request holds.
public static class RequestFanOut
{
    public const int MaxConcurrency = 8;

    // Results keep the order of the source items.
    public static async Task<TResult[]> SelectAsync<TSource, TResult>(
        IReadOnlyList<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> projector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projector);

        if (source.Count == 0)
        {
            return [];
        }

        var run = new FanOutRun<TSource, TResult>(source, projector, cancellationToken);
        var workers = Enumerable.Range(0, Math.Min(MaxConcurrency, source.Count)).Select(_ => run.RunWorkerAsync());
        await Task.WhenAll(workers).ConfigureAwait(false);
        return run.Results;
    }

    private sealed class FanOutRun<TSource, TResult>(
        IReadOnlyList<TSource> source,
        Func<TSource, CancellationToken, Task<TResult>> projector,
        CancellationToken cancellationToken)
    {
        private int _nextIndex = -1;
        private int _faulted;

        public TResult[] Results { get; } = new TResult[source.Count];

        // After one read fails no worker starts another, so the error surfaces without further reads.
        public async Task RunWorkerAsync()
        {
            while (Volatile.Read(ref _faulted) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = Interlocked.Increment(ref _nextIndex);
                if (index >= source.Count)
                {
                    return;
                }

                try
                {
                    Results[index] = await projector(source[index], cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Volatile.Write(ref _faulted, 1);
                    throw;
                }
            }
        }
    }
}
